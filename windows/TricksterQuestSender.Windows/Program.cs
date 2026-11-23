using Velopack;

namespace QuestSenderWin;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/update/uninstall hooks of the installer.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
