using System.Data.Common;
using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Queue;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.SocialPost.Credentials;

/// <summary>One field as the dashboard sees it. Value is filled only for fields that are not secret.</summary>
public sealed record FieldState(string Key, string Label, bool Secret, bool Required, bool IsSet, string Source, string? Value);

public sealed record NetworkState(string Name, bool Enabled, string EnabledSource, bool Configured, int MaxLength, IReadOnlyList<FieldState> Fields);

/// <summary>
/// Reads a network's settings from configuration first, then from the database. Secrets saved from the
/// backoffice are encrypted with Data Protection. Nothing here returns a stored secret to the browser.
/// </summary>
public sealed class CredentialStore(
    IConfiguration configuration,
    IScopeProvider scopeProvider,
    IDataProtectionProvider protection,
    ILogger<CredentialStore> logger)
{
    public const string EnabledKey = "Enabled";
    public const string TemplateKey = "Template";
    private const int MaxValueLength = 4000;

    private readonly IDataProtector _protector = protection.CreateProtector("BaryoDev.Umbraco.SocialPost.Credentials.v1");

    private static CredentialField TemplateField(ISocialConnector c) => new(TemplateKey, "Message template", false, false, c.DefaultTemplate);

    private static readonly string[] EndpointKeys = ["ServiceUrl", "InstanceUrl"];

    private static CredentialField? EndpointField(ISocialConnector c) => c.Fields.FirstOrDefault(f => EndpointKeys.Contains(f.Key));

    private static string HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.Authority.ToLowerInvariant() : "";

    /// <summary>The endpoint a secret would be sent to: configuration, then stored, then the default. Empty when the network has none.</summary>
    private string EndpointHost(ISocialConnector c, Dictionary<string, string> stored)
    {
        CredentialField? f = EndpointField(c);
        if (f is null)
        {
            return "";
        }

        string? value = Section(c.Name)[f.Key];
        if (string.IsNullOrWhiteSpace(value) && !EndpointLocked(c, f))
        {
            stored.TryGetValue(f.Key, out value);
        }

        return HostOf(string.IsNullOrWhiteSpace(value) ? f.Default : value);
    }

    // A secret from configuration must only go where configuration (or the built-in default) says.
    private bool SecretFromConfig(ISocialConnector c) =>
        c.Fields.Any(f => f.Secret && !string.IsNullOrWhiteSpace(Section(c.Name)[f.Key]));

    private bool EndpointLocked(ISocialConnector c, CredentialField f) =>
        EndpointKeys.Contains(f.Key) && string.IsNullOrWhiteSpace(Section(c.Name)[f.Key]) && SecretFromConfig(c);

    private IConfigurationSection Section(string network) => configuration.GetSection($"{SocialPostOptions.NetworksSection}:{network}");

    /// <summary>The values a connector needs, defaults included. Used by the worker, never sent to the browser.</summary>
    public Dictionary<string, string> Resolve(ISocialConnector connector)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> stored = LoadStored(connector);
        foreach (CredentialField f in connector.Fields.Append(TemplateField(connector)))
        {
            string? value = Section(connector.Name)[f.Key];
            if (string.IsNullOrWhiteSpace(value) && !EndpointLocked(connector, f) && stored.TryGetValue(f.Key, out string? s))
            {
                value = s;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = f.Default;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                result[f.Key] = value;
            }
        }

        return result;
    }

    public bool IsEnabled(string network)
    {
        string? value = Section(network)[EnabledKey];
        if (string.IsNullOrWhiteSpace(value))
        {
            LoadStored(null, network).TryGetValue(EnabledKey, out value);
        }

        return bool.TryParse(value, out bool on) && on;
    }

    public static bool IsConfigured(ISocialConnector c, IReadOnlyDictionary<string, string> settings) =>
        c.Fields.Where(f => f.Required).All(f => settings.TryGetValue(f.Key, out string? v) && !string.IsNullOrWhiteSpace(v));

    public NetworkState Describe(ISocialConnector connector)
    {
        Dictionary<string, string> stored = LoadStored(connector);
        var fields = new List<FieldState>();
        foreach (CredentialField f in connector.Fields.Append(TemplateField(connector)))
        {
            bool inConfig = !string.IsNullOrWhiteSpace(Section(connector.Name)[f.Key]);
            bool locked = EndpointLocked(connector, f);
            bool inStore = !locked && stored.ContainsKey(f.Key);
            string source = inConfig ? "config" : locked ? "locked" : inStore ? "stored" : f.Default is not null ? "default" : "none";
            string? value = f.Secret ? null : inConfig ? Section(connector.Name)[f.Key] : inStore ? stored[f.Key] : f.Default;
            fields.Add(new FieldState(f.Key, f.Label, f.Secret, f.Required, inConfig || inStore, source, value));
        }

        string enabledSource = !string.IsNullOrWhiteSpace(Section(connector.Name)[EnabledKey]) ? "config"
            : stored.ContainsKey(EnabledKey) ? "stored" : "none";
        return new NetworkState(
            connector.Name, IsEnabled(connector.Name), enabledSource,
            IsConfigured(connector, Resolve(connector)), connector.MaxLength, fields);
    }

    /// <summary>Blank values leave a field alone. Fields that come from configuration are not changed.</summary>
    public void Save(ISocialConnector connector, bool? enabled, IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> clear)
    {
        var known = connector.Fields.Append(TemplateField(connector)).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        using var scope = scopeProvider.CreateScope();

        void Put(string field, string value, bool secret, string boundHost = "")
        {
            CredentialRow? row = scope.Database.SingleOrDefault<CredentialRow>("WHERE Network = @0 AND Field = @1", connector.Name, field);
            string stored = secret ? _protector.Protect(boundHost + "\n" + value) : value;
            if (row is not null)
            {
                row.Value = stored;
                row.Protected = secret;
                scope.Database.Update(row);
                return;
            }

            try
            {
                scope.Database.Insert(new CredentialRow { Network = connector.Name, Field = field, Value = stored, Protected = secret });
            }
            catch (DbException)
            {
                // Another save of the same field won the race and hit the unique index. Update its row instead.
                row = scope.Database.SingleOrDefault<CredentialRow>("WHERE Network = @0 AND Field = @1", connector.Name, field) ?? throw new InvalidOperationException("The credential row could not be written.");
                row.Value = stored;
                row.Protected = secret;
                scope.Database.Update(row);
            }
        }

        // A new endpoint must not inherit old secrets, or they would be sent to a host the old owner never chose.
        CredentialField? endpoint = EndpointField(connector);
        Dictionary<string, string> current = LoadStored(connector);
        string oldHost = EndpointHost(connector, current);
        string? newValue = endpoint is not null && values.TryGetValue(endpoint.Key, out string? nv) && !string.IsNullOrWhiteSpace(nv)
            && string.IsNullOrWhiteSpace(Section(connector.Name)[endpoint.Key]) ? nv.Trim() : null;
        if (newValue is not null && HostOf(newValue) != oldHost && endpoint is not null && EndpointLocked(connector, endpoint))
        {
            throw new ArgumentException(
                $"The {connector.Name} secret comes from configuration, so the address has to be set in configuration too.");
        }

        if (newValue is not null && HostOf(newValue) != oldHost)
        {
            foreach (CredentialField secret in connector.Fields.Where(f => f.Secret))
            {
                scope.Database.Execute("DELETE FROM socialPostCredential WHERE Network = @0 AND Field = @1", connector.Name, secret.Key);
            }
        }

        string host = newValue is not null ? HostOf(newValue) : oldHost;

        foreach ((string key, string? raw) in values)
        {
            if (!known.TryGetValue(key, out CredentialField? field) || string.IsNullOrWhiteSpace(raw)
                || !string.IsNullOrWhiteSpace(Section(connector.Name)[field.Key]))
            {
                continue;
            }

            if (raw.Length > MaxValueLength)
            {
                throw new ArgumentException($"{field.Label} is too long.");
            }

            Put(field.Key, raw.Trim(), field.Secret, host);
        }

        foreach (string key in clear)
        {
            if (known.TryGetValue(key, out CredentialField? field))
            {
                scope.Database.Execute("DELETE FROM socialPostCredential WHERE Network = @0 AND Field = @1", connector.Name, field.Key);
            }
        }

        if (enabled is bool on && string.IsNullOrWhiteSpace(Section(connector.Name)[EnabledKey]))
        {
            Put(EnabledKey, on ? "true" : "false", false);
        }

        scope.Complete();
    }

    private Dictionary<string, string> LoadStored(ISocialConnector connector) => LoadStored(connector, connector.Name);

    // With no connector only plain fields are read; secrets are skipped.
    private Dictionary<string, string> LoadStored(ISocialConnector? connector, string network)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secrets = new List<CredentialRow>();
        using (var scope = scopeProvider.CreateScope(autoComplete: true))
        {
            foreach (CredentialRow row in scope.Database.Fetch<CredentialRow>("WHERE Network = @0", network))
            {
                if (row.Protected)
                {
                    secrets.Add(row);
                }
                else
                {
                    result[row.Field] = row.Value;
                }
            }
        }

        if (connector is null)
        {
            return result;
        }

        // A secret is only used for the host it was saved for.
        string host = EndpointHost(connector, result);
        foreach (CredentialRow row in secrets)
        {
            try
            {
                string payload = _protector.Unprotect(row.Value);
                int split = payload.IndexOf('\n');
                if (split < 0 || payload[..split] != host)
                {
                    logger.LogWarning("A stored {Network} {Field} does not match the configured host and was ignored", network, row.Field);
                    continue;
                }

                result[row.Field] = payload[(split + 1)..];
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // The key ring changed. The field shows as not set and has to be entered again.
                logger.LogWarning("A stored {Network} {Field} could not be decrypted", network, row.Field);
            }
        }

        return result;
    }
}
