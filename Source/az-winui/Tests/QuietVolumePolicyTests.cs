using AccessibilityZoom;

internal static class QuietVolumePolicyTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(Math.Abs(QuietVolumePolicy.Reduce(.8f, 30) - .24f) < .0001f,
            "Quiet mode must scale an 80 percent level to 24 percent.");
        check(Math.Abs(QuietVolumePolicy.Reduce(.1f, 30) - .03f) < .0001f,
            "Quiet mode must scale even a level below the configured percentage.");
        check(QuietVolumePolicy.Reduce(.8f, 0) == 0 && QuietVolumePolicy.Reduce(.8f, 100) == .8f,
            "Zero and one hundred percent must map to silence and the original level.");
        check(QuietVolumePolicy.GetBaseline(0, true, .75f) == .75f,
            "Holding after a legacy mute must use the remembered level.");
        check(QuietVolumePolicy.GetBaseline(0, false, .75f) == 0,
            "An unmuted zero level must stay silent even with an old mute restore level.");
        check(QuietVolumePolicy.GetBaseline(.01f, true, .75f) == .01f,
            "A muted session with a positive level must keep that level.");
    }
}
