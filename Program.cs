using Adw;
using Boxy;
using Gio;

return BoxyProgram.Run(args);

public static class BoxyProgram
{
    public static int Run(string[] args)
    {
        var app = Adw.Application.New("io.github.axoisaxo.Boxy", ApplicationFlags.FlagsNone);
        var boxy = new BoxyApp(app, Boxy.Core.AppStateStore.Load());
        app.OnActivate += (_, _) => boxy.Activate();
        return app.RunWithSynchronizationContext(args);
    }
}
