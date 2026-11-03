using System.Reflection;

namespace Pollyanna.Infrastructure;

/// <summary>
/// <c>Pollyanna help</c>: what an operator with only the image needs (spec §11). DEPLOYMENT.md is
/// the long-form guide; this is the short reference, and must stay free of anyone's deployment.
/// </summary>
public static class Help
{
    /// <summary>The release, from <c>-p:Version</c> in CI; <c>0.0.0-dev</c> for local builds.</summary>
    public static string Version { get; } =
        typeof(Help).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";

    public static string Text => $$"""
        Pollyanna {{Version}}: a small self-hosted gift exchange with a sealed draw.

        The full guide is /app/DEPLOYMENT.md in this image.

        USAGE
          /app/Pollyanna                    Start the web app (plain HTTP on port 8080).
          /app/Pollyanna help               Show this help (also --help, -h).
          /app/Pollyanna healthcheck [url]  Exit 0 if the running app answers /healthz.
          /app/Pollyanna backup <file>      Write a consistent copy of the live database.

        SETTINGS (environment variables, all required except EMAIL below)
          POLLYANNA_PUBLIC_URL      The address people open, e.g. https://pollyanna.example.
                                  A bare origin, no path. Put a TLS-terminating reverse
                                  proxy in front; forwarded headers are not trusted.
          POLLYANNA_OIDC_ISSUER     The OIDC provider's issuer URL (https).
          POLLYANNA_OIDC_CLIENT_ID  The client ID registered at the provider.
          POLLYANNA_OIDC_CLIENT_SECRET or POLLYANNA_OIDC_CLIENT_SECRET_FILE
                                  The client secret, or the path of a file holding it.
                                  Set exactly one.
          POLLYANNA_DATA_DIR        Directory for pollyanna.db and keys/ (sign-in key ring).
                                  Must be writable. Suggested: /data
          POLLYANNA_DRAW_KEY_FILE   Path of the draw key, e.g. /draw-key/draw.key. Created
                                  on first start if absent, so its directory must be
                                  writable then. Keep it on a different mount from the
                                  data directory.

        EMAIL (optional; leave POLLYANNA_SMTP_HOST unset and email notifications are off)
          POLLYANNA_SMTP_HOST       The mail relay that sends notification emails. With it,
                                  port, TLS and from address are required too; without
                                  it, the other email settings must be unset.
          POLLYANNA_SMTP_PORT       e.g. 587 for starttls, 465 for tls.
          POLLYANNA_SMTP_TLS        starttls, tls (implicit TLS) or none.
          POLLYANNA_SMTP_USERNAME   Relay login, if it needs one; then also a password.
          POLLYANNA_SMTP_PASSWORD or POLLYANNA_SMTP_PASSWORD_FILE
                                  The relay password, or the path of a file holding it.
                                  Set at most one.
          POLLYANNA_MAIL_FROM       The from address, e.g. Pollyanna <pollyanna@home.example>.
          A relay that can't be reached is logged and retried; the app still starts.
          Emails wait in the database for at most 24 hours; nothing records sent mail.

        MOUNTS AND USER
          The app runs as uid/gid 1654, which has no account on the host. Create the
          two host directories owned by that number:
            sudo install -d -o 1654 -g 1654 -m 700 data draw-key
          Mount them at the container paths named in the settings above. With rootless
          Docker or user-namespace remapping, uid 1654 maps to a different host uid;
          give the directories to that one instead.
          The image runs fine with a read-only root filesystem plus a tmpfs on /tmp.

        THE DRAW KEY
          32 raw bytes (not base64 or hex), mode 0600. It seals who drew whom: only
          someone with both the database and the key can link giver to recipient.
          Back it up once, right after the first start, separately from the data
          backups (as text: base64 draw.key).
          If it's lost or replaced, every drawn but unrevealed slip becomes unreadable
          and the only recovery is a redraw. "Created a new draw key" in the log should
          appear once in an installation's life; if it appears again, stop Pollyanna and
          put the old key back before anyone draws.

        REGISTERING THE OIDC CLIENT
          Client type      Confidential (with a client secret)
          Redirect URI     {public URL}/auth/callback, exactly
          Flow             Authorization code with PKCE (S256)
          Scopes           openid profile email
          Client auth      Client secret in the request body (client_secret_post)
          Post-logout URI  None needed
          PAR              Used if the provider advertises it
          Limiting who may sign in is the provider's job; Pollyanna checks no groups.

        EMAIL CLAIMS
          Invitations by email match a person's "email" claim when they sign in,
          unless the provider sends email_verified: false. Without an email_verified
          claim they still match.
          Re-linking (a recreated provider account, or a new provider, finding its
          existing Pollyanna person by email) needs email_verified: true. If your
          provider has a setting for sending email_verified, turn it on.

        HEALTH CHECK (Compose)
            healthcheck:
              test: ["CMD", "/app/Pollyanna", "healthcheck"]
              interval: 30s
              timeout: 10s
              start_period: 30s
              retries: 3

        BACKUP
          Don't copy pollyanna.db while the app runs (it uses WAL). Instead:
            docker exec <container> /app/Pollyanna backup /data/backups/pollyanna-$(date +%F).db
          Then copy that file and the keys/ directory off the data directory to your
          backup location. The command never overwrites a file. Restore the database
          only with the same draw key it was used with.
        """;
}
