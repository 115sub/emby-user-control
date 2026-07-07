#pragma warning disable CS0618
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Session;

namespace EmbyUserControl
{
    public class ServerEntryPoint : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IApplicationPaths _applicationPaths;

        private Timer _timer;
        private readonly object _lock = new object();
        private List<PlayRecord> _playRecords = new List<PlayRecord>();
        private string _lastCheckedDate;
        private string _recordsFilePath;
        private DateTime _lastTimerRunUtc;
        private bool _recordsDirty;

        public ServerEntryPoint(
            ISessionManager sessionManager,
            IUserManager userManager,
            ILogManager logManager,
            IJsonSerializer jsonSerializer,
            IApplicationPaths applicationPaths)
        {
            _sessionManager = sessionManager;
            _userManager = userManager;
            _logger = logManager.GetLogger("EmbyUserControl");
            _jsonSerializer = jsonSerializer;
            _applicationPaths = applicationPaths;

            _recordsFilePath = Path.Combine(_applicationPaths.PluginConfigurationsPath, "play_records.json");
            _lastCheckedDate = DateTime.Today.ToString("yyyy-MM-dd");
            _lastTimerRunUtc = DateTime.UtcNow;
        }

        public void Run()
        {
            _logger.Info("EmbyUserControl 插件服务正在启动...");

            // 1. 加载历史时长记录
            LoadRecords();

            // 2. 第一次运行进行自愈和状态同步
            lock (_lock)
            {
                var now = DateTime.Now;
                SyncAndHealUsers(now.ToString("yyyy-MM-dd"), now);
                LogConfiguredUserRemainingTimes(now.ToString("yyyy-MM-dd"), "启动配置快照");
            }

            // 3. 订阅事件
            _sessionManager.PlaybackStart += OnPlaybackStart;

            // 4. 初始化定时器，每 10 秒执行一次检测
            _timer = new Timer(OnTimerCallback, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            _logger.Info("EmbyUserControl 插件服务启动完成。");
        }

        private void OnTimerCallback(object state)
        {
            try
            {
                string today = DateTime.Today.ToString("yyyy-MM-dd");

                lock (_lock)
                {
                    var nowUtc = DateTime.UtcNow;
                    var elapsedSeconds = Math.Max(0, (nowUtc - _lastTimerRunUtc).TotalSeconds);
                    _lastTimerRunUtc = nowUtc;

                    // 1. 跨日重置检查
                    if (today != _lastCheckedDate)
                    {
                        _logger.Info($"检测到日期变更，从 {_lastCheckedDate} 跨入 {today}。开始重置时长并恢复插件锁定的用户权限...");
                        _lastCheckedDate = today;
                        ResetAllLimitedUsers(today);
                    }

                    var now = DateTime.Now;

                    // 2. 状态自愈：同步并纠正用户权限状态
                    SyncAndHealUsers(today, now);

                    // 3. 时间累加与超限检测
                    UpdateAndCheckActiveSessions(today, now, elapsedSeconds);

                    // 4. 仅在记录发生变化时持久化
                    if (_recordsDirty)
                    {
                        SaveRecords();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("定时检测周期执行中遇到异常: ", ex);
            }
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            if (e.Session == null || string.IsNullOrEmpty(e.Session.UserName)) return;

            try
            {
                string username = e.Session.UserName;
                var now = DateTime.Now;
                string today = now.ToString("yyyy-MM-dd");

                lock (_lock)
                {
                    // 检查该用户是否受限
                    var limit = FindLimit(username);

                    if (limit == null)
                    {
                        _logger.Info($"[播放开始] 用户 [{username}] 未配置限制，允许播放。SessionId={e.Session.Id}。");
                    }
                    else
                    {
                        var record = GetRecord(username, today, false);
                        var watchedSeconds = record == null ? 0 : record.WatchedSeconds;
                        string reason;
                        _logger.Info($"[播放开始] 命中受限用户 [{username}]。SessionId={e.Session.Id}，今日已播放={watchedSeconds:0.0} 秒，规则={DescribeLimit(limit)}。");
                        if (HasTriggeredLimit(limit, record, now, out reason))
                        {
                            _logger.Info($"受限用户 [{username}] 尝试在已触发限制后播放。原因={reason}。立即实施阻断。");
                            BlockUserPlayback(e.Session, username, reason);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("拦截 PlaybackStart 事件异常: ", ex);
            }
        }

        private void UpdateAndCheckActiveSessions(string today, DateTime now, double elapsedSeconds)
        {
            var sessions = _sessionManager.Sessions.ToList();
            var activeUserGroups = sessions
                .Where(s => !string.IsNullOrEmpty(s.UserName) && s.NowPlayingItem != null && (s.PlayState == null || !s.PlayState.IsPaused))
                .GroupBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (activeUserGroups.Count == 0)
            {
                return;
            }

            _logger.Info($"[时间计算] 开始本轮检测。日期={today}，距离上轮={elapsedSeconds:0.0} 秒，Session总数={sessions.Count}，活跃播放用户数={activeUserGroups.Count}。");

            foreach (var userGroup in activeUserGroups)
            {
                string username = userGroup.Key;

                // 查找是否在限制名单中
                var limit = FindLimit(username);

                if (limit == null)
                {
                    continue;
                }

                var record = GetRecord(username, today, true);

                var beforeWatchedSeconds = record.WatchedSeconds;
                string reason;
                if (HasTriggeredLimit(limit, record, now, out reason))
                {
                    _logger.Info($"受限用户 [{username}] 已触发限制。原因={reason}，累计={record.WatchedSeconds:0.0} 秒，规则={DescribeLimit(limit)}。执行断流与弹窗。");
                    foreach (var session in userGroup)
                    {
                        BlockUserPlayback(session, username, reason);
                    }
                    continue;
                }

                if (HasDurationLimit(limit))
                {
                    // 同一用户多端同时播放时，只按自然时间累加一次。
                    record.WatchedSeconds += elapsedSeconds;
                    _recordsDirty = true;
                }

                if (HasTriggeredLimit(limit, record, now, out reason))
                {
                    _logger.Info($"受限用户 [{username}] 已触发限制。原因={reason}，上轮累计={beforeWatchedSeconds:0.0} 秒，本轮累计={record.WatchedSeconds:0.0} 秒，规则={DescribeLimit(limit)}。执行断流与弹窗。");
                    foreach (var session in userGroup)
                    {
                        BlockUserPlayback(session, username, reason);
                    }
                }
                else if (HasDurationLimit(limit))
                {
                    var remainingSeconds = Math.Max(0, limit.LimitMinutes * 60 - record.WatchedSeconds);
                    _logger.Info($"[时间计算] 用户 [{username}] 正在播放，本轮增加={elapsedSeconds:0.0} 秒，累计={record.WatchedSeconds:0.0} 秒，剩余={remainingSeconds:0.0} 秒。");
                }
            }
        }

        private void BlockUserPlayback(SessionInfo session, string username, string reason)
        {
            SendLimitMessageAndStop(session);

            // 3. 物理切断播放权限与远程访问权限 (UserPolicy)
            var user = _userManager.Users.FirstOrDefault(u => string.Equals(u.Name, username, StringComparison.OrdinalIgnoreCase));
            if (user != null)
            {
                var record = GetRecord(username, DateTime.Now.ToString("yyyy-MM-dd"), true);
                ApplyUserLock(user, record, reason);
            }
            else
            {
                _logger.Warn($"尝试关闭用户 [{username}] 的播放权限，但未在用户列表中找到该用户。");
            }

            RevokeUserAccess(username, user);
        }

        private void SendLimitMessageAndStop(SessionInfo session)
        {
            string message = string.IsNullOrEmpty(Plugin.Instance.Configuration.TimeoutMessage)
                ? "当前不在允许播放时间段内，或今日播放时长已达上限，播放已被终止。"
                : Plugin.Instance.Configuration.TimeoutMessage;

            var displayCommand = new GeneralCommand
            {
                Name = "DisplayMessage",
                Arguments = new Dictionary<string, string>
                {
                    { "Header", "播放控制" },
                    { "Text", message },
                    { "TimeoutMs", "6000" }
                }
            };

            try
            {
                _sessionManager.SendGeneralCommand(null, session.Id, displayCommand, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warn($"向 Session {session.Id} 发送 DisplayMessage 命令失败: {ex.Message}");
            }

            // 2. 发送 Stop 播放命令
            var stopCommand = new GeneralCommand
            {
                Name = "Stop"
            };

            try
            {
                _sessionManager.SendGeneralCommand(null, session.Id, stopCommand, CancellationToken.None);
                _logger.Info($"已向 Session {session.Id} 发送 GeneralCommand Stop。");
            }
            catch (Exception ex)
            {
                _logger.Warn($"向 Session {session.Id} 发送 Stop 播放命令失败: {ex.Message}");
            }

            var playstateStopCommand = new PlaystateRequest
            {
                Command = PlaystateCommand.Stop
            };

            try
            {
                _sessionManager.SendPlaystateCommand(null, session.Id, playstateStopCommand, CancellationToken.None);
                _logger.Info($"已向 Session {session.Id} 发送 PlaystateCommand Stop。");
            }
            catch (Exception ex)
            {
                _logger.Warn($"向 Session {session.Id} 发送 PlaystateCommand Stop 失败: {ex.Message}");
            }
        }

        private void RevokeUserAccess(string username, MediaBrowser.Controller.Entities.User user)
        {
            if (user == null)
            {
                return;
            }

            try
            {
                _sessionManager.RevokeUserTokens(user.InternalId, null);
                _logger.Info($"已通过 SessionManager 撤销用户 [{username}] 的所有访问 Token。UserInternalId={user.InternalId}。");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"撤销用户 [{username}] 访问 Token 失败: ", ex);
            }

            var userSessions = _sessionManager.Sessions
                .Where(s => string.Equals(s.UserName, username, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var activeSession in userSessions)
            {
                try
                {
                    _sessionManager.ReportSessionEnded(activeSession.Id);
                    _logger.Info($"已标记用户 [{username}] 的 Session 结束。SessionId={activeSession.Id}，Client={activeSession.Client}，DeviceName={activeSession.DeviceName}。");
                }
                catch (Exception ex)
                {
                    _logger.Warn($"标记 Session {activeSession.Id} 结束失败: {ex.Message}");
                }
            }
        }

        private void ResetAllLimitedUsers(string today)
        {
            var allUsers = _userManager.Users.ToList();
            foreach (var limit in GetConfiguredLimits())
            {
                string username = limit.Username;
                var todayRecord = GetRecord(username, today, true);
                if (Math.Abs(todayRecord.WatchedSeconds) > 0.001)
                {
                    todayRecord.WatchedSeconds = 0;
                    _recordsDirty = true;
                }

                var user = allUsers.FirstOrDefault(u => string.Equals(u.Name, limit.Username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                {
                    continue;
                }

                var lockRecord = GetManagedLockRecord(username) ?? todayRecord;
                ReleaseUserLock(user, lockRecord, "[跨日重置]");
            }
        }

        private void SyncAndHealUsers(string today, DateTime now)
        {
            var allUsers = _userManager.Users.ToList();

            // 1. 同步受限用户状态：必须同时满足时长限制和允许播放时间段。
            foreach (var limit in GetConfiguredLimits())
            {
                string username = limit.Username;
                var user = allUsers.FirstOrDefault(u => string.Equals(u.Name, username, StringComparison.OrdinalIgnoreCase));
                if (user == null) continue;

                var record = GetRecord(username, today, false);
                string reason;
                bool hasTriggered = HasTriggeredLimit(limit, record, now, out reason);

                try
                {
                    if (hasTriggered)
                    {
                        var lockRecord = GetRecord(username, today, true);
                        bool changed = ApplyUserLock(user, lockRecord, reason);
                        bool stoppedSessions = StopActiveSessionsForUser(username);
                        if (changed || stoppedSessions)
                        {
                            RevokeUserAccess(username, user);
                        }
                        if (changed)
                        {
                            _logger.Info($"[状态自愈] 用户 [{username}] 已触发限制，执行补锁。原因={reason}。");
                        }
                    }
                    else
                    {
                        var lockRecord = GetManagedLockRecord(username);
                        if (lockRecord != null)
                        {
                            ReleaseUserLock(user, lockRecord, "[状态自愈]");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"[状态自愈] 处理用户 [{username}] 权限同步异常: ", ex);
                }
            }

            // 2. 修复已从限制列表移除的用户，防止插件锁定状态异常残留。
            foreach (var user in allUsers)
            {
                string username = user.Name;
                bool isLimited = GetConfiguredLimits()
                    .Any(l => string.Equals(l.Username, username, StringComparison.OrdinalIgnoreCase));

                if (!isLimited)
                {
                    var lockRecord = GetManagedLockRecord(username);
                    if (lockRecord != null)
                    {
                        try
                        {
                            ReleaseUserLock(user, lockRecord, "[自愈修复]");
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException($"[自愈修复] 恢复普通用户 [{username}] 权限异常: ", ex);
                        }
                    }
                }
            }
        }

        private List<UserLimit> GetConfiguredLimits()
        {
            return Plugin.Instance.Configuration.UserLimits ?? new List<UserLimit>();
        }

        private UserLimit FindLimit(string username)
        {
            return GetConfiguredLimits()
                .FirstOrDefault(l => l != null
                    && !string.IsNullOrWhiteSpace(l.Username)
                    && string.Equals(l.Username, username, StringComparison.OrdinalIgnoreCase));
        }

        private PlayRecord GetRecord(string username, string date, bool create)
        {
            var record = _playRecords.FirstOrDefault(r =>
                string.Equals(r.Username, username, StringComparison.OrdinalIgnoreCase) && r.Date == date);

            if (record == null && create)
            {
                record = new PlayRecord
                {
                    Username = username,
                    Date = date,
                    WatchedSeconds = 0
                };
                _playRecords.Add(record);
                _recordsDirty = true;
            }

            return record;
        }

        private PlayRecord GetManagedLockRecord(string username)
        {
            return _playRecords.FirstOrDefault(r =>
                string.Equals(r.Username, username, StringComparison.OrdinalIgnoreCase)
                && (r.PluginLocked || r.HasOriginalPolicySnapshot));
        }

        private bool HasDurationLimit(UserLimit limit)
        {
            return limit != null && limit.LimitMinutes > 0;
        }

        private bool TryParseTime(string value, out TimeSpan time)
        {
            time = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Trim().Split(':');
            if (parts.Length != 2)
            {
                return false;
            }

            int hour;
            int minute;
            if (!int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute))
            {
                return false;
            }

            if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
            {
                return false;
            }

            time = new TimeSpan(hour, minute, 0);
            return true;
        }

        private bool TryGetAllowedTimeRange(UserLimit limit, out TimeSpan start, out TimeSpan end, out string displayRange)
        {
            start = TimeSpan.Zero;
            end = TimeSpan.Zero;
            displayRange = null;

            if (limit == null)
            {
                return false;
            }

            var startText = limit.AllowedStartTime;
            var endText = limit.AllowedEndTime;

            // 兼容 1.0.1.0 的截止时间配置：截止 22:00 等价于允许 00:00-22:00。
            if ((string.IsNullOrWhiteSpace(startText) || string.IsNullOrWhiteSpace(endText))
                && !string.IsNullOrWhiteSpace(limit.CutoffTime))
            {
                startText = "00:00";
                endText = limit.CutoffTime;
            }

            if (!TryParseTime(startText, out start) || !TryParseTime(endText, out end) || start == end)
            {
                return false;
            }

            displayRange = $"{startText}-{endText}";
            return true;
        }

        private bool IsWithinAllowedTimeRange(TimeSpan current, TimeSpan start, TimeSpan end)
        {
            if (start < end)
            {
                return current >= start && current < end;
            }

            // 跨午夜时间段，例如 22:00-02:00。
            return current >= start || current < end;
        }

        private bool HasTriggeredLimit(UserLimit limit, PlayRecord record, DateTime now, out string reason)
        {
            if (HasDurationLimit(limit) && record != null && record.WatchedSeconds >= limit.LimitMinutes * 60)
            {
                reason = $"每日播放时长已达到 {limit.LimitMinutes} 分钟";
                return true;
            }

            TimeSpan start;
            TimeSpan end;
            string displayRange;
            if (TryGetAllowedTimeRange(limit, out start, out end, out displayRange)
                && !IsWithinAllowedTimeRange(now.TimeOfDay, start, end))
            {
                reason = $"当前时间不在允许播放时间段 {displayRange} 内";
                return true;
            }

            reason = null;
            return false;
        }

        private string DescribeLimit(UserLimit limit)
        {
            var parts = new List<string>();
            if (HasDurationLimit(limit))
            {
                parts.Add($"每日 {limit.LimitMinutes} 分钟");
            }

            TimeSpan start;
            TimeSpan end;
            string displayRange;
            if (TryGetAllowedTimeRange(limit, out start, out end, out displayRange))
            {
                parts.Add($"允许时段 {displayRange}");
            }

            return parts.Count == 0 ? "未启用有效限制" : string.Join("，", parts.ToArray());
        }

        private bool ApplyUserLock(MediaBrowser.Controller.Entities.User user, PlayRecord record, string reason)
        {
            try
            {
                var policy = user.Policy;
                if (!record.HasOriginalPolicySnapshot)
                {
                    record.OriginalEnableMediaPlayback = policy.EnableMediaPlayback;
                    record.OriginalEnableRemoteAccess = policy.EnableRemoteAccess;
                    record.OriginalIsDisabled = policy.IsDisabled;
                    record.HasOriginalPolicySnapshot = true;
                    _recordsDirty = true;
                }

                if (!record.PluginLocked || record.LockReason != reason)
                {
                    _recordsDirty = true;
                }
                record.PluginLocked = true;
                record.LockReason = reason;

                var changed = false;
                if (policy.EnableMediaPlayback)
                {
                    policy.EnableMediaPlayback = false;
                    changed = true;
                }

                if (policy.EnableRemoteAccess)
                {
                    policy.EnableRemoteAccess = false;
                    changed = true;
                }

                if (!policy.IsDisabled)
                {
                    policy.IsDisabled = true;
                    changed = true;
                }

                if (changed)
                {
                    _userManager.UpdateUserPolicy(user.InternalId, policy);
                    _logger.Info($"已锁定用户 [{user.Name}] 的播放权限、远程访问和登录状态。原因={reason}。");
                }

                return changed;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"修改用户 [{user.Name}] 权限失败: ", ex);
                return false;
            }
        }

        private bool ReleaseUserLock(MediaBrowser.Controller.Entities.User user, PlayRecord record, string stage)
        {
            if (record == null || (!record.PluginLocked && !record.HasOriginalPolicySnapshot))
            {
                return false;
            }

            try
            {
                var policy = user.Policy;
                var changed = false;

                if (policy.EnableMediaPlayback != record.OriginalEnableMediaPlayback)
                {
                    policy.EnableMediaPlayback = record.OriginalEnableMediaPlayback;
                    changed = true;
                }

                if (policy.EnableRemoteAccess != record.OriginalEnableRemoteAccess)
                {
                    policy.EnableRemoteAccess = record.OriginalEnableRemoteAccess;
                    changed = true;
                }

                if (policy.IsDisabled != record.OriginalIsDisabled)
                {
                    policy.IsDisabled = record.OriginalIsDisabled;
                    changed = true;
                }

                if (changed)
                {
                    _userManager.UpdateUserPolicy(user.InternalId, policy);
                    _logger.Info($"{stage} 已按插件锁定前状态恢复用户 [{user.Name}] 权限。");
                }

                record.PluginLocked = false;
                record.HasOriginalPolicySnapshot = false;
                record.LockReason = null;
                _recordsDirty = true;

                return changed;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"{stage} 恢复用户 [{user.Name}] 权限失败: ", ex);
                return false;
            }
        }

        private bool StopActiveSessionsForUser(string username)
        {
            var userSessions = _sessionManager.Sessions
                .Where(s => string.Equals(s.UserName, username, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var session in userSessions)
            {
                SendLimitMessageAndStop(session);
            }

            return userSessions.Count > 0;
        }

        private void LogConfiguredUserRemainingTimes(string today, string stage)
        {
            var limits = GetConfiguredLimits();
            if (limits.Count == 0)
            {
                _logger.Info($"[{stage}] 当前没有配置任何受限用户。");
                return;
            }

            foreach (var limit in limits)
            {
                if (string.IsNullOrWhiteSpace(limit.Username))
                {
                    _logger.Warn($"[{stage}] 发现一条用户名为空的限制配置，限制分钟数={limit.LimitMinutes}。");
                    continue;
                }

                var record = _playRecords.FirstOrDefault(r => string.Equals(r.Username, limit.Username, StringComparison.OrdinalIgnoreCase) && r.Date == today);
                var watchedSeconds = record == null ? 0 : record.WatchedSeconds;
                var remainingText = "未启用时长限制";
                if (HasDurationLimit(limit))
                {
                    var limitSeconds = limit.LimitMinutes * 60;
                    var remainingSeconds = Math.Max(0, limitSeconds - watchedSeconds);
                    remainingText = $"时长剩余={remainingSeconds:0.0} 秒";
                }

                _logger.Info($"[{stage}] 受限用户 [{limit.Username}] 今日已播放={watchedSeconds:0.0} 秒，规则={DescribeLimit(limit)}，{remainingText}。");
            }
        }

        private void LoadRecords()
        {
            try
            {
                if (File.Exists(_recordsFilePath))
                {
                    using (var stream = File.OpenRead(_recordsFilePath))
                    {
                        var records = _jsonSerializer.DeserializeFromStream<List<PlayRecord>>(stream);
                        if (records != null)
                        {
                            _playRecords = records;
                            _logger.Info($"成功从文件加载了 {_playRecords.Count} 条已播放时长记录。");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("加载 play_records.json 失败，初始化空列表: ", ex);
            }
            _playRecords = new List<PlayRecord>();
        }

        private void SaveRecords()
        {
            try
            {
                string directory = Path.GetDirectoryName(_recordsFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempFilePath = _recordsFilePath + ".tmp";
                using (var stream = File.Create(tempFilePath))
                {
                    _jsonSerializer.SerializeToStream(_playRecords, stream);
                }

                if (File.Exists(_recordsFilePath))
                {
                    try
                    {
                        File.Replace(tempFilePath, _recordsFilePath, null);
                    }
                    catch
                    {
                        File.Copy(tempFilePath, _recordsFilePath, true);
                        File.Delete(tempFilePath);
                    }
                }
                else
                {
                    File.Move(tempFilePath, _recordsFilePath);
                }

                _recordsDirty = false;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("保存 play_records.json 时遇到异常: ", ex);
            }
        }

        public void Dispose()
        {
            _logger.Info("EmbyUserControl 正在清理资源...");

            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }

            if (_sessionManager != null)
            {
                _sessionManager.PlaybackStart -= OnPlaybackStart;
            }

            lock (_lock)
            {
                SaveRecords();
            }

            _logger.Info("EmbyUserControl 资源清理完毕。");
        }
    }
}
