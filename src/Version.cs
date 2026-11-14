using System.Reflection;

// Bump AppVersion.Text, then push a matching tag (v1.2.0 etc.) - the Release workflow builds and publishes it,
// and every installed launcher updates itself on next start.
[assembly: AssemblyTitle("TDA Launcher")]
[assembly: AssemblyProduct("TDA Launcher")]
[assembly: AssemblyVersion(TDALauncher.AppVersion.Text + ".0")]
[assembly: AssemblyFileVersion(TDALauncher.AppVersion.Text + ".0")]

namespace TDALauncher
{
    static class AppVersion
    {
        public const string Text = "1.1.1";
    }
}
