namespace EmbyUserControl
{
    public class PlayRecord
    {
        public string Username { get; set; }
        public string Date { get; set; }
        public double WatchedSeconds { get; set; }
        public bool PluginLocked { get; set; }
        public bool HasOriginalPolicySnapshot { get; set; }
        public bool OriginalEnableMediaPlayback { get; set; }
        public bool OriginalEnableRemoteAccess { get; set; }
        public bool OriginalIsDisabled { get; set; }
        public string LockReason { get; set; }
    }
}
