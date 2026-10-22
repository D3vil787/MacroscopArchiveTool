using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MacroscopArchiveTool.Core;

partial class Program
{
    internal static void UpdaterParseAndApply()
    {
        R.True(Updater.IsNewer("0.2.2", "0.2.1"), "0.2.2 новее 0.2.1");
        R.True(Updater.IsNewer("0.10.0", "0.9.9"), "числовое сравнение");
        R.True(!Updater.IsNewer("0.2.1", "0.2.1"), "та же версия");
        R.True(!Updater.IsNewer("garbage", "0.2.1"), "мусор не новее");

        var root = Path.Combine(Path.GetTempPath(), "mat_upd_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pkg = Path.Combine(root, "pkg"); Directory.CreateDirectory(pkg);
            File.WriteAllText(Path.Combine(pkg, "MacroscopArchiveTool.exe"), "new gui");
            File.WriteAllText(Path.Combine(pkg, "mat.exe"), "new cli");
            File.WriteAllText(Path.Combine(pkg, "scan_report.bat"), "new bat");
            var zip = Path.Combine(root, Updater.AssetName);
            ZipFile.CreateFromDirectory(pkg, zip);
            string sha;
            using (var s = File.OpenRead(zip)) sha = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            var size = new FileInfo(zip).Length;

            var json = $$"""
            {"tag_name":"v0.9.0","html_url":"https://example/rel","body":"notes","draft":false,"prerelease":false,
             "assets":[{"name":"other.zip","size":1,"browser_download_url":"x"},
                       {"name":"{{Updater.AssetName}}","size":{{size}},"digest":"sha256:{{sha}}","browser_download_url":"https://example/a.zip"}]}
            """;
            using var doc = JsonDocument.Parse(json);
            var u = Updater.Parse(doc.RootElement, "0.2.1")!;
            R.Eq("0.9.0", u.Version, "версия из тега");
            R.Eq(sha, u.Sha256, "SHA-256 из digest");
            R.True(Updater.Parse(doc.RootElement, "0.9.0") == null, "не предлагать ту же версию");

            Updater.Verify(zip, u);
            R.Throws(() => Updater.Verify(zip, u with { Sha256 = new string('0', 64) }), "чужая контрольная сумма");

            var app = Path.Combine(root, "app"); Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "MacroscopArchiveTool.exe"), "old gui");
            File.WriteAllText(Path.Combine(app, "mat.json"), "{ settings }");
            var replaced = Updater.Apply(zip, app);
            R.Eq(3, replaced.Count, "заменено файлов");
            R.Eq("new gui", File.ReadAllText(Path.Combine(app, "MacroscopArchiveTool.exe")), "новый exe");
            R.True(File.Exists(Path.Combine(app, "MacroscopArchiveTool.exe.old")), "старый exe переименован");
            R.Eq("{ settings }", File.ReadAllText(Path.Combine(app, "mat.json")), "настройки не тронуты");
            Updater.CleanupOldFiles(app);
            R.True(!File.Exists(Path.Combine(app, "MacroscopArchiveTool.exe.old")), "старый файл удалён при следующем запуске");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }
}
