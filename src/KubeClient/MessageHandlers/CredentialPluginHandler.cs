#if NETSTANDARD2_1

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using KubeClient.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KubeClient.MessageHandlers
{
    /// <summary>
    ///     HTTP message handler that runs a client-go credential plugin to obtain a bearer token and adds it to outgoing requests.
    /// </summary>
    public class CredentialPluginHandler
        : BearerTokenHandler
    {
        /// <summary>
        ///     An object used to synchronise access to handler state.
        /// </summary>
        readonly object _stateLock = new object();

        /// <summary>
        ///     The expected version of the client-go credential plugin API.
        /// </summary>
        /// <remarks>
        ///     Client must return this version exactly.
        /// </remarks>
        readonly string _expectedApiVersion;

        /// <summary>
        ///     The command to execute in order to invoke the credential plugin.
        /// </summary>
        readonly string _pluginCommand;

        /// <summary>
        ///     The arguments (if any) for the credential plugin.
        /// </summary>
        readonly IReadOnlyList<string> _pluginCommandArguments;

        /// <summary>
        ///     The environment variables (if any) for the credential plugin.
        /// </summary>
        readonly IReadOnlyDictionary<string, string> _pluginCommandEnvironment;

        /// <summary>
        ///     The current access token (if any).
        /// </summary>
        string _accessToken;

        /// <summary>
        ///     The UTC date/time that the access token expires.
        /// </summary>
        DateTime? _accessTokenExpiresUtc;

        /// <summary>
        ///     Create a new <see cref="CredentialPluginHandler"/>.
        /// </summary>
        /// <param name="expectedApiVersion">
        ///     The expected version of the client-go credential plugin API.
        /// </param>
        /// <param name="pluginCommand">
        ///     The command to execute in order to obtain the access token for outgoing requests.
        /// </param>
        /// <param name="pluginCommandArguments">
        ///     The arguments (if any) for the access-token command.
        /// </param>
        /// <param name="pluginCommandEnvironment">
        ///     The Go-style selector used to retrieve the access token from the command output.
        /// </param>
        public CredentialPluginHandler(string expectedApiVersion, string pluginCommand, IReadOnlyList<string> pluginCommandArguments, IReadOnlyDictionary<string, string> pluginCommandEnvironment)
        {
            if ( String.IsNullOrWhiteSpace(expectedApiVersion) )
                throw new ArgumentException($"Argument cannot be null, empty, or entirely composed of whitespace: {nameof(expectedApiVersion)}.", nameof(expectedApiVersion));

            if ( String.IsNullOrWhiteSpace(pluginCommand) )
                throw new ArgumentException("Argument cannot be null, empty, or entirely composed of whitespace: 'accessTokenCommand'.", nameof(pluginCommand));

            _expectedApiVersion = expectedApiVersion;
            _pluginCommand = pluginCommand;
            _pluginCommandArguments = pluginCommandArguments?.ToArray() ?? new string[0];
            _pluginCommandEnvironment = pluginCommandEnvironment?.ToDictionary(entry => entry.Key, entry => entry.Value) ?? new Dictionary<string, string>();
        }

        /// <summary>
        ///     Obtain a bearer token to use for authentication.
        /// </summary>
        /// <param name="cancellationToken">
        ///     An optional <see cref="CancellationToken"/> that can be used to cancel the operation.
        /// </param>
        /// <returns>
        ///     The access token.
        /// </returns>
        protected override async Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            string accessToken;
            DateTime? accessTokenExpiresUtc;

            // Capture snapshot of access token / expiry.
            lock ( _stateLock )
            {
                accessToken = _accessToken;
                accessTokenExpiresUtc = _accessTokenExpiresUtc;
            }

            if ( !String.IsNullOrWhiteSpace(accessToken) && accessTokenExpiresUtc > DateTime.UtcNow )
                return accessToken;

            cancellationToken.ThrowIfCancellationRequested();

            ProcessStartInfo pluginCommandInfo = new ProcessStartInfo(_pluginCommand)
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach ( string pluginCommandArgument in _pluginCommandArguments )
                pluginCommandInfo.ArgumentList.Add(pluginCommandArgument);

            foreach ( string variableName in _pluginCommandEnvironment.Keys )
                pluginCommandInfo.EnvironmentVariables[variableName] = _pluginCommandEnvironment[variableName];

            using ( Process pluginCommandProcess = Process.Start(pluginCommandInfo) )
            {
                int exitCode = await pluginCommandProcess.WaitForExitAsync(cancellationToken, killIfCancelled: true);
                if ( exitCode != 0 )
                {
                    // We omit the command's STDOUT / STDERR from this exception message because they may contain sensitive information!

                    throw new KubeClientException(
                        $"Failed to execute credential-plugin command '{_pluginCommand} {String.Join(' ', _pluginCommandArguments)}' (process exited with code {exitCode})."
                    );
                }

                string standardOutput = await pluginCommandProcess.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // Ensure command output is JSON
                JObject outputJson;
                try
                {
                    outputJson = JObject.Parse(standardOutput);
                }
                catch ( JsonReaderException invalidJson )
                {
                    throw new KubeClientException($"Failed to parse output of credential plug-in command '{_pluginCommand} {String.Join(' ', _pluginCommandArguments)}' (not valid JSON).",
                        innerException: invalidJson
                    );
                }

                string actualApiVersion = outputJson.Value<string>("apiVersion");
                if ( !String.Equals(actualApiVersion, _expectedApiVersion) )
                    throw new KubeClientException($"Failed to parse output of credential plug-in command '{_pluginCommand} {String.Join(' ', _pluginCommandArguments)}' (plug-in returned unexpected API version '{actualApiVersion}'; expected '{_expectedApiVersion}').");

                string actualResultKind = outputJson.Value<string>("kind");
                if ( !String.Equals(actualResultKind, "ExecCredential", StringComparison.OrdinalIgnoreCase) )
                    throw new KubeClientException($"Failed to parse output of credential plug-in command '{_pluginCommand} {String.Join(' ', _pluginCommandArguments)}' (plug-in returned unexpected result kind '{actualResultKind}'; expected 'ExecCredential').");

                JObject resultStatus = outputJson.Value<JObject>("status");
                if ( resultStatus == null )
                    throw new KubeClientException($"Failed to parse output of credential plug-in command '{_pluginCommand} {String.Join(' ', _pluginCommandArguments)}' (plug-in returned unexpected result; missing 'status' property).");

                if ( resultStatus.Property("clientCertificateData") != null )
                    throw new KubeClientException($"Credential plug-in command '{_pluginCommand} {String.Join(' ', _pluginCommandArguments)}' returned a client certificate, not an access token (this is not currently supported).");

                accessToken = resultStatus.Value<string>("token");
                if ( accessToken == null )
                {
                    throw new KubeClientException(
                        $"Failed to find access-token in output of command '{_pluginCommand} {String.Join(' ', _pluginCommandArguments)}' ('status' object is missing 'token' property)."
                            + Environment.NewLine
                            + standardOutput
                    );
                }

                // Credential plugins can provide a credential-expiry timestamp, but they don't have to.
                string credentialExpiryTimestamp = resultStatus.Value<string>("expirationTimestamp");

                try
                {
                    accessTokenExpiresUtc = ParseExpiryTimestamp(credentialExpiryTimestamp);
                }
                catch ( FormatException invalidTimestampFormat )
                {
                    throw new KubeClientException($"Failed to parse access-token expiry timestamp in output of command '{_pluginCommand} {String.Join(' ', _pluginCommandArguments)}' (expiry timestamp '{credentialExpiryTimestamp}' is not in a recognised format).",
                        innerException: invalidTimestampFormat
                    );
                }

                // OK, both access token and expiry are good; update atomically.
                lock ( _stateLock )
                {
                    _accessToken = accessToken;
                    _accessTokenExpiresUtc = accessTokenExpiresUtc;
                }

                return accessToken;
            }
        }

        /// <summary>
        ///     Parse an credential-expiry timestamp (assumes the timestamp is already UTC).
        /// </summary>
        /// <param name="credentialExpiryTimestamp">
        ///     The credential-expiry timestamp (in RFC3339 format).
        /// </param>
        /// <returns>
        ///     The parsed <see cref="DateTime"/>, or <c>null</c> if <paramref name="credentialExpiryTimestamp"/> is <c>null</c>.
        /// </returns>
        DateTime? ParseExpiryTimestamp(string credentialExpiryTimestamp)
        {
            if (credentialExpiryTimestamp == null)
                return null;

            return DateTime.Parse(credentialExpiryTimestamp,
                provider: CultureInfo.InvariantCulture,
                styles: DateTimeStyles.AssumeUniversal
            );
        }

        /// <summary>
        ///     Convert a Go-style selector to a JPath-style selector.
        /// </summary>
        /// <param name="goSelector">
        ///     The Go-style selector (e.g. "{.foo.bar}").
        /// </param>
        /// <returns>
        ///     The JPath-style selector (e.g. "$.foo.bar").
        /// </returns>
        static string JPathFromGoSelector(string goSelector)
        {
            if ( String.IsNullOrWhiteSpace(goSelector) )
                throw new ArgumentException("Argument cannot be null, empty, or entirely composed of whitespace: 'goSelector'.", nameof(goSelector));

            string jpathSelector = goSelector;

            if ( jpathSelector[0] == '{' && jpathSelector[jpathSelector.Length - 1] == '}' )
                jpathSelector = jpathSelector.Substring(1, jpathSelector.Length - 2);

            if ( jpathSelector[0] == '.' )
                jpathSelector = '$' + jpathSelector;

            return jpathSelector;
        }
    }
}

#endif // NETSTANDARD2_1