namespace FileTransit;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Distinct from the separately built "Portly Desktop"/FileDock (Local\PortlyDesktop-…), so both can run side by side.
        using var instance = new Mutex(true, "Local\\FileTransit-" + Environment.UserName, out bool first);
        if (!first) { MessageBox.Show("FileTransit zaten açık. Sunucuları mevcut pencerede ayrı oturumlarda açabilirsiniz.", "FileTransit"); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
