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
                SyncAndHealUsers(DateTime.Today.ToString("yyyy-MM-dd"));
                LogConfiguredUserRemainingTimes(DateTime.Today.ToString("yyyy-MM-dd"), "启动配置快照");
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
                        _logger.Info($"检测到日期变更，从 {_lastCheckedDate} 跨入 {today}。开始重置时长并恢复所有用户播放权限...");
                        _lastCheckedDate = today;
                        ResetAllLimitedUsers(today);
                    }

                    // 2. 状态自愈：同步并纠正用户 EnableMediaPlayback 状态
                    SyncAndHealUsers(today);

                    // 3. 时间累加与超限检测
                    UpdateAndCheckActiveSessions(today, elapsedSeconds);

                    // 4. 定期持久化
                    SaveRecords();
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
                string today = DateTime.Today.ToString("yyyy-MM-dd");

                lock (_lock)
                {
                    // 检查该用户是否受限
                    var limit = Plugin.Instance.Configuration.UserLimits
                        .FirstOrDefault(l => string.Equals(l.Username, username, StringComparison.OrdinalIgnoreCase));

                    if (limit == null)
                    {
                        _logger.Info($"[播放开始] 用户 [{username}] 未配置限制，允许播放。SessionId={e.Session.Id}。");
                    }
                    else
                    {
                        // 查找今日记录
                        var record = _playRecords.FirstOrDefault(r => string.Equals(r.Username, username, StringComparison.OrdinalIgnoreCase) && r.Date == today);
                        var watchedSeconds = record == null ? 0 : record.WatchedSeconds;
                        var limitSeconds = limit.LimitMinutes * 60;
                        var remainingSeconds = Math.Max(0, limitSeconds - watchedSeconds);
                        _logger.Info($"[播放开始] 命中受限用户 [{username}]。SessionId={e.Session.Id}，今日已播放={watchedSeconds:0.0} 秒，限制={limitSeconds} 秒，剩余={remainingSeconds:0.0} 秒。");
                        if (record != null && record.WatchedSeconds >= limit.LimitMinutes * 60)
                        {
                            _logger.Info($"受限用户 [{username}] 尝试在已超额情况下播放。今日已播放 {record.WatchedSeconds} 秒，限制 {limit.LimitMinutes} 分钟。立即实施阻断。");
                            BlockUserPlayback(e.Session, username);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("拦截 PlaybackStart 事件异常: ", ex);
            }
        }

        private void UpdateAndCheckActiveSessions(string today, double elapsedSeconds)
        {
            var sessions = _sessionManager.Sessions.ToList();
            var activeSessions = sessions
                .Where(s => !string.IsNullOrEmpty(s.UserName) && s.NowPlayingItem != null && (s.PlayState == null || !s.PlayState.IsPaused))
                .ToList();

            _logger.Info($"[时间计算] 开始本轮检测。日期={today}，距离上轮={elapsedSeconds:0.0} 秒，Session总数={sessions.Count}，活跃播放Session数={activeSessions.Count}。");

            LogConfiguredUserRemainingTimes(today, "时间计算前");

            foreach (var session in activeSessions)
            {
                string username = session.UserName;

                // 查找是否在限制名单中
                var limit = Plugin.Instance.Configuration.UserLimits
                    .FirstOrDefault(l => string.Equals(l.Username, username, StringComparison.OrdinalIgnoreCase));

                if (limit == null)
                {
                    _logger.Info($"[时间计算] 活跃播放用户 [{username}] 未配置限制，跳过计时。SessionId={session.Id}。");
                    continue;
                }

                // 找到限制，按实际定时器间隔累加，避免 timer 延迟导致时间计算漂移。
                var record = _playRecords.FirstOrDefault(r => string.Equals(r.Username, username, StringComparison.OrdinalIgnoreCase) && r.Date == today);
                if (record == null)
                {
                    record = new PlayRecord
                    {
                        Username = username,
                        Date = today,
                        WatchedSeconds = 0
                    };
                    _playRecords.Add(record);
                }

                var limitSeconds = limit.LimitMinutes * 60;
                var beforeWatchedSeconds = record.WatchedSeconds;
                record.WatchedSeconds += elapsedSeconds;
                var remainingSeconds = Math.Max(0, limitSeconds - record.WatchedSeconds);
                _logger.Info($"[时间计算] 用户 [{username}] 正在播放。SessionId={session.Id}，本轮增加={elapsedSeconds:0.0} 秒，累计={record.WatchedSeconds:0.0}/{limitSeconds} 秒，剩余={remainingSeconds:0.0} 秒。");

                // 判断是否超时
                if (record.WatchedSeconds >= limit.LimitMinutes * 60)
                {
                    _logger.Info($"受限用户 [{username}] 播放时长已超额。上轮累计 {beforeWatchedSeconds:0.0} 秒，本轮累计 {record.WatchedSeconds:0.0} 秒，限制 {limit.LimitMinutes} 分钟。执行断流与弹窗。");
                    BlockUserPlayback(session, username);
                }
            }

            LogConfiguredUserRemainingTimes(today, "时间计算后");
        }

        private void BlockUserPlayback(SessionInfo session, string username)
        {
            // 1. 发送提示弹框
            string message = string.IsNullOrEmpty(Plugin.Instance.Configuration.TimeoutMessage)
                ? "您今日的播放时长已达上限，播放已被终止。"
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
            }
            catch (Exception ex)
            {
                _logger.Warn($"向 Session {session.Id} 发送 Stop 播放命令失败: {ex.Message}");
            }

            // 3. 物理切断播放权限 (UserPolicy)
            var user = _userManager.Users.FirstOrDefault(u => string.Equals(u.Name, username, StringComparison.OrdinalIgnoreCase));
            if (user != null)
            {
                try
                {
                    var policy = user.Policy;
                    if (policy.EnableMediaPlayback)
                    {
                        policy.EnableMediaPlayback = false;
                        _userManager.UpdateUserPolicy(user.InternalId, policy);
                        _logger.Info($"已关闭用户 [{username}] 的播放权限。");
                    }
                    else
                    {
                        _logger.Info($"用户 [{username}] 的播放权限已经处于关闭状态，无需重复关闭。");
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"修改用户 [{username}] 权限失败: ", ex);
                }
            }
            else
            {
                _logger.Warn($"尝试关闭用户 [{username}] 的播放权限，但未在用户列表中找到该用户。");
            }
        }

        private void ResetAllLimitedUsers(string today)
        {
            foreach (var limit in Plugin.Instance.Configuration.UserLimits)
            {
                string username = limit.Username;
                var record = _playRecords.FirstOrDefault(r => string.Equals(r.Username, username, StringComparison.OrdinalIgnoreCase));
                if (record != null)
                {
                    record.Date = today;
                    record.WatchedSeconds = 0;
                }
            }

            // 恢复所有用户的播放权限
            var allUsers = _userManager.Users.ToList();
            foreach (var user in allUsers)
            {
                try
                {
                    var policy = user.Policy;
                    if (!policy.EnableMediaPlayback)
                    {
                        policy.EnableMediaPlayback = true;
                        _userManager.UpdateUserPolicy(user.InternalId, policy);
                        _logger.Info($"[跨日重置] 已恢复用户 [{user.Name}] 的播放权限。");
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"[跨日重置] 恢复用户 [{user.Name}] 权限失败: ", ex);
                }
            }
        }

        private void SyncAndHealUsers(string today)
        {
            var allUsers = _userManager.Users.ToList();

            // 1. 先修复那些被限额且超时的用户的权限（确保状态完全同步）
            foreach (var limit in Plugin.Instance.Configuration.UserLimits)
            {
                string username = limit.Username;
                var user = allUsers.FirstOrDefault(u => string.Equals(u.Name, username, StringComparison.OrdinalIgnoreCase));
                if (user == null) continue;

                var record = _playRecords.FirstOrDefault(r => string.Equals(r.Username, username, StringComparison.OrdinalIgnoreCase) && r.Date == today);
                bool hasExceeded = record != null && record.WatchedSeconds >= limit.LimitMinutes * 60;

                try
                {
                    var policy = user.Policy;
                    if (hasExceeded && policy.EnableMediaPlayback)
                    {
                        policy.EnableMediaPlayback = false;
                        _userManager.UpdateUserPolicy(user.InternalId, policy);
                        _logger.Info($"[状态自愈] 用户 [{username}] 已超时但权限未关闭，执行补锁关闭权限。");
                    }
                    else if (!hasExceeded && !policy.EnableMediaPlayback)
                    {
                        policy.EnableMediaPlayback = true;
                        _userManager.UpdateUserPolicy(user.InternalId, policy);
                        _logger.Info($"[状态自愈] 用户 [{username}] 未超时但权限处于关闭状态，执行解锁开放权限。");
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"[状态自愈] 处理用户 [{username}] 权限同步异常: ", ex);
                }
            }

            // 2. 修复非限制列表中的用户，防止其权限异常残留为 false
            foreach (var user in allUsers)
            {
                string username = user.Name;
                bool isLimited = Plugin.Instance.Configuration.UserLimits
                    .Any(l => string.Equals(l.Username, username, StringComparison.OrdinalIgnoreCase));

                if (!isLimited)
                {
                    try
                    {
                        var policy = user.Policy;
                        if (!policy.EnableMediaPlayback)
                        {
                            policy.EnableMediaPlayback = true;
                            _userManager.UpdateUserPolicy(user.InternalId, policy);
                            _logger.Info($"[自愈修复] 发现未受限普通用户 [{username}] 的播放权限被禁用，现已自动恢复开启。");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException($"[自愈修复] 恢复普通用户 [{username}] 权限异常: ", ex);
                    }
                }
            }
        }

        private void LogConfiguredUserRemainingTimes(string today, string stage)
        {
            var limits = Plugin.Instance.Configuration.UserLimits ?? new List<UserLimit>();
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
                var limitSeconds = limit.LimitMinutes * 60;
                var remainingSeconds = Math.Max(0, limitSeconds - watchedSeconds);
                _logger.Info($"[{stage}] 受限用户 [{limit.Username}] 今日已播放={watchedSeconds:0.0} 秒，限制={limitSeconds} 秒（{limit.LimitMinutes} 分钟），剩余={remainingSeconds:0.0} 秒。");
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

                using (var stream = File.Create(_recordsFilePath))
                {
                    _jsonSerializer.SerializeToStream(_playRecords, stream);
                }
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
