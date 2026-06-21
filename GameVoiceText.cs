namespace Pelican_XVASynth
{
    public class GameVoiceText
    {
        public string gameId;
        public int pitch;
        public float rate;
        public string text;
        public string voiceId;
        public float vol;
        public string lang = "en"; // FIX: Provide default language fallback for v3.0 models
    }
}