namespace NovelCleaner.Server.Configuration;

public class AuthOptions
{
    public PasswordAuthOptions Password { get; set; } = new();
    public OidcOptions Oidc { get; set; } = new();

    /// <summary>
    /// Optional bootstrap admin. When BOTH email and password are set AND no
    /// admin exists yet, the user is seeded on startup. Otherwise the app
    /// shows the first-run setup page so the operator can create the admin
    /// interactively. Default is empty — no env-driven defaults.
    /// </summary>
    public string? FirstAdminEmail { get; set; }
    public string? FirstAdminPassword { get; set; }

    /// <summary>
    /// When true (default), every authenticated user is allowed to use the
    /// configured drop folder. When false, only users with the
    /// <c>BookDrop</c> role (granted by an admin or by OIDC group membership
    /// — see <see cref="OidcOptions.DropFolderGroups"/>) and admins
    /// themselves can. Admins always have the permission.
    /// </summary>
    public bool DefaultBookDrop { get; set; } = true;
}

public class PasswordAuthOptions
{
    public bool Enabled { get; set; } = true;
    public bool AllowSelfRegister { get; set; } = false;
}

public class OidcOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Human-readable name for the SSO provider, shown on the login page in
    /// the "Continue with {DisplayName}" button. Default is "single sign-on";
    /// override to e.g. "Authentik", "Okta", "your company SSO".
    /// </summary>
    public string DisplayName { get; set; } = "single sign-on";

    /// <summary>
    /// IdP issuer base URL. The handler will discover endpoints at
    /// {Authority}/.well-known/openid-configuration unless MetadataAddress
    /// or the manual endpoints below are provided.
    /// </summary>
    public string Authority { get; set; } = "";

    /// <summary>
    /// Explicit URL of the OpenID Connect discovery document.
    /// Overrides Authority-based discovery when set.
    /// </summary>
    public string? MetadataAddress { get; set; }

    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string CallbackPath { get; set; } = "/signin-oidc";
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>
    /// Name of the claim that carries the user's group memberships.
    /// Only consulted when <see cref="AdminGroups"/> is non-empty.
    /// </summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>
    /// Optional. If empty, OIDC does not manage the Admin role at all — admin
    /// promotions made manually in /admin/users stick across logins. If
    /// non-empty, users in any of these groups get the Admin role on login,
    /// and users no longer in any of them lose it.
    /// </summary>
    public List<string> AdminGroups { get; set; } = [];

    /// <summary>
    /// Optional. Groups whose members are granted the <c>BookDrop</c>
    /// permission on login. Only consulted when
    /// <see cref="AuthOptions.DefaultBookDrop"/> is false. If empty (and
    /// DefaultBookDrop is false), only admins can use the drop folder.
    /// </summary>
    public List<string> DropFolderGroups { get; set; } = [];

    public bool AutoProvision { get; set; } = true;

    // ---- Manual endpoint overrides (for IdPs without discovery, or when you
    // want to skip the network round-trip). All four endpoint URLs must be
    // set together; Issuer falls back to Authority when omitted.
    public string? Issuer { get; set; }
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? UserInfoEndpoint { get; set; }
    public string? JwksUri { get; set; }
    public string? EndSessionEndpoint { get; set; }

    public bool HasManualEndpoints =>
        !string.IsNullOrWhiteSpace(AuthorizationEndpoint) &&
        !string.IsNullOrWhiteSpace(TokenEndpoint) &&
        !string.IsNullOrWhiteSpace(JwksUri);
}

public class OpdsOptions
{
    /// <summary>
    /// Allow OPDS connections to private/loopback/link-local addresses.
    /// Useful for self-hosted setups where the OPDS server lives on the same LAN.
    /// Defaults to false to keep the SSRF guard active.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; } = false;
}

public class StorageOptions
{
    /// <summary>Where the SQLite DB and Data Protection keys live.</summary>
    public string DataDirectory { get; set; } = "/data";

    /// <summary>Where uploaded EPUBs and cleaned outputs live (kept separate so
    /// you can mount books on a different volume than your DB).</summary>
    public string BooksDirectory { get; set; } = "/books";

    public string UploadDirectory => Path.Combine(BooksDirectory, "uploads");
    public string OutputDirectory => Path.Combine(BooksDirectory, "outputs");
    /// <summary>
    /// One git repo per novel lives under here, named by novel id. Backs the
    /// page editor — file per page, commits as edit history.
    /// </summary>
    public string RepoDirectory   => Path.Combine(BooksDirectory, "repos");
    public string KeysDirectory   => Path.Combine(DataDirectory,  "keys");

    public long MaxUploadBytes { get; set; } = 200L * 1024 * 1024; // 200 MB
}
