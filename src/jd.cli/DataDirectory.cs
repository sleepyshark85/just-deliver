using System.Security.Cryptography;
using System.Text;
using jd.core.records;
using jd.resolver.release;

namespace jd.cli;

/// <summary>
/// jd's data directory, where it keeps the deployment state, the state's key and the records, so nothing has to be set on the machine
/// (docs/architecture/connections.md): <c>jd</c> in the local application data folder, or <c>JD_DATA_DIR</c>. The one place it is resolved; what is
/// in it is created on first use.
/// </summary>
internal sealed class DataDirectory
{
    internal const string Variable = "JD_DATA_DIR";

    private const string Name = "jd";
    private const string StateFolder = "state";
    private const string KeyFile = "state.key";
    private const string RecordsFolder = "records";

    // The passphrase's random bytes: 256 bits.
    private const int KeyLength = 32;

    private DataDirectory(string root) => Root = root;

    /// <summary>The directory itself.</summary>
    public string Root { get; }

    /// <summary>The release, qualification and approval records.</summary>
    public IRecordStore Records => new LocalRecordStore(Path.Combine(Root, RecordsFolder));

    public static DataDirectory Resolve(Func<string, string?> environmentVariable) =>
        new(environmentVariable(Variable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), Name));

    /// <summary>
    /// The state folder and the file holding the passphrase its secrets are encrypted with. The passphrase is random, generated once and readable
    /// by its owner only; it is never replaced, or the state it encrypts could not be read.
    /// </summary>
    public (string StateDirectory, string KeyFile) PrepareState()
    {
        var state = Directory.CreateDirectory(Path.Combine(Root, StateFolder)).FullName;
        var key = Path.Combine(Path.GetFullPath(Root), KeyFile);
        if (!File.Exists(key))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(key, options);
            stream.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyLength))));
        }

        return (state, key);
    }
}
