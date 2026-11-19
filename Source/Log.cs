namespace BaseGroundsPlus
{
    internal static class Log
    {
        private const string Prefix = "[BaseGrounds+] ";

        public static void Info(string msg) { UnityEngine.Debug.Log(Prefix + msg); }
        public static void Warn(string msg) { UnityEngine.Debug.LogWarning(Prefix + msg); }
        public static void Error(string msg) { UnityEngine.Debug.LogError(Prefix + msg); }

        public static void Dbg(string msg)
        {
            if (Settings.DebugLogging) UnityEngine.Debug.Log(Prefix + msg);
        }

        public static void Screen(string msg)
        {
            Info(msg);
            if (Settings.ShowMessages)
                ScreenMessages.PostScreenMessage("BaseGrounds+: " + msg, 5f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
