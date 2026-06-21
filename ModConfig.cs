namespace Pelican_XVASynth
{
    public class ModConfig
    {
        public bool EnableMod { get; set; } = true;
        public string NPCGameVoices { get; set; } = "";
        public int MaxSecondsWait { get; set; } = 60;
        public int MillisecondsPrepare { get; set; } = 1500;
        public int MaxLettersToPrepare { get; set; } = 200;
    }
}
