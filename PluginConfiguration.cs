using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace EmbyUserControl
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public List<UserLimit> UserLimits { get; set; }
        public string TimeoutMessage { get; set; }

        public PluginConfiguration()
        {
            UserLimits = new List<UserLimit>();
            TimeoutMessage = "当前不在允许播放时间段内，或今日播放时长已达上限，播放已被终止。";
        }
    }
}
