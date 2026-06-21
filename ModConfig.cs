using System.Collections.Generic;

namespace Pelican_XVASynth
{
    public class ModConfig
    {
        public bool EnableMod { get; set; } = true;
        public int MaxSecondsWait { get; set; } = 60;
        public int MillisecondsPrepare { get; set; } = 1500;
        public int MaxLettersToPrepare { get; set; } = 200;

        public Dictionary<string, VoiceSetup> Voices { get; set; } = new Dictionary<string, VoiceSetup>();
    }

    public class VoiceSetup
    {
        public string Voice { get; set; } = "";
        public string Game { get; set; } = "";
        public float Pitch { get; set; } = 0.0f; // Added field for dynamic frequency tweaking
    }
}
