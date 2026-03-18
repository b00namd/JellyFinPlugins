using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTubbing.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTubbing.Services;

/// <summary>
/// Handles Google OAuth2 Device Authorization Grant (RFC 8628) for YouTube read-only access.
/// No redirect URI required – user enters a code at accounts.google.com/device.
/// </summary>
public class OAuthService
{
    private const string DeviceCodeEndpoint = "https://oauth2.googleapis.com/device/code";
    private const string TokenEndpoint      = "https://oauth2.googleapis.com/token";
    private const string Scope              = "https://www.googleapis.com/auth/youtube.readonly";
    private const string DeviceGrantType    = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<OAuthService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthService"/> class.
    /// </summary>
    public OAuthService(IHttpClientFactory http, ILogger<OAuthService> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>Returns true when a valid (or refreshable) token is stored.</summary>
    public bool IsAuthorized =>
        !string.IsNullOrEmpty(Plugin.Instance?.Configuration.OAuthRefreshToken);

    // -----------------------------------------------------------------------
    // Device Authorization Grant
    // -----------------------------------------------------------------------

    /// <summary>
    /// Starts the device authorization flow.
    /// Returns the device code response containing user_code and verification_url.
    /// </summary>
    public async Task<DeviceCodeResponse?> StartDeviceAuthAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.OAuthClientId))
            return null;

        try
        {
            var client = _http.CreateClient("jellytubbing");
            var resp = await client.PostAsync(DeviceCodeEndpoint, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"] = config.OAuthClientId,
                    ["scope"]     = Scope,
                }), ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Device code request failed ({Status}): {Body}", resp.StatusCode, body);
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device code request exception");
            return null;
        }
    }

    /// <summary>
    /// Polls the token endpoint once for the given device code.
    /// Returns "success", "pending", "slow_down", "denied", or "expired".
    /// </summary>
    public async Task<string> PollDeviceAsync(string deviceCode, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return "error";

        try
        {
            var client = _http.CreateClient("jellytubbing");
            var resp = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["client_id"]     = config.OAuthClientId,
                    ["client_secret"] = config.OAuthClientSecret,
                    ["device_code"]   = deviceCode,
                    ["grant_type"]    = DeviceGrantType,
                }), ct);

            if (resp.IsSuccessStatusCode)
            {
                var token = await resp.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken: ct);
                if (token is not null)
                {
                    StoreTokens(config, token);
                    return "success";
                }
                return "error";
            }

            // Parse error response
            var err = await resp.Content.ReadFromJsonAsync<OAuthErrorResponse>(cancellationToken: ct);
            return err?.Error switch
            {
                "authorization_pending" => "pending",
                "slow_down"             => "slow_down",
                "access_denied"         => "denied",
                "expired_token"         => "expired",
                _                       => "error",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device poll exception");
            return "error";
        }
    }

    // -----------------------------------------------------------------------
    // Token access + refresh
    // -----------------------------------------------------------------------

    /// <summary>Returns a valid access token, refreshing automatically if expired.</summary>
    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(config.OAuthRefreshToken))
            return null;

        if (!string.IsNullOrEmpty(config.OAuthAccessToken) &&
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() < config.OAuthTokenExpiryUnix)
            return config.OAuthAccessToken;

        return await RefreshAsync(config, ct);
    }

    /// <summary>Clears all stored OAuth tokens.</summary>
    public void Revoke()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null) return;
        config.OAuthAccessToken     = string.Empty;
        config.OAuthRefreshToken    = string.Empty;
        config.OAuthTokenExpiryUnix = 0;
        Plugin.Instance!.SaveConfiguration();
    }

    // -----------------------------------------------------------------------

    private async Task<string?> RefreshAsync(Configuration.PluginConfiguration config, CancellationToken ct)
    {
        try
        {
            var client = _http.CreateClient("jellytubbing");
            var resp = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["refresh_token"] = config.OAuthRefreshToken,
                    ["client_id"]     = config.OAuthClientId,
                    ["client_secret"] = config.OAuthClientSecret,
                    ["grant_type"]    = "refresh_token",
                }), ct);

            if (!resp.IsSuccessStatusCode) return null;

            var token = await resp.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken: ct);
            if (token is null) return null;

            StoreTokens(config, token);
            return token.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token refresh failed");
            return null;
        }
    }

    private static void StoreTokens(Configuration.PluginConfiguration config, OAuthTokenResponse token)
    {
        config.OAuthAccessToken     = token.AccessToken;
        if (!string.IsNullOrEmpty(token.RefreshToken))
            config.OAuthRefreshToken = token.RefreshToken;
        config.OAuthTokenExpiryUnix = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn - 60).ToUnixTimeSeconds();
        Plugin.Instance!.SaveConfiguration();
    }
}
