using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IdentityModel.OidcClient;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SSO_Auth.Config;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SSO_Auth.Api;

/// <summary>
/// The sso api controller.
/// </summary>
[ApiController]
[Route("[controller]")]
public class SSOController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SSOController> _logger;
    private static readonly IDictionary<string, TimedAuthorizeState> StateManager = new Dictionary<string, TimedAuthorizeState>();

    /// <summary>
    /// Initializes a new instance of the <see cref="SSOController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{SSOController}"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public SSOController(ILogger<SSOController> logger, ISessionManager sessionManager, IUserManager userManager)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _logger = logger;
        _logger.LogInformation("SSO Controller initialized");
    }

    /// <summary>
    /// The GET endpoint for OpenID provider to callback to. Returns a webpage that parses client data and completes auth.
    /// </summary>
    /// <param name="provider">The ID of the provider which will use the callback information.</param>
    /// <param name="state">The current request state.</param>
    /// <returns>A webpage that will complete the client-side flow.</returns>
    // Actually a GET: https://github.com/IdentityModel/IdentityModel.OidcClient/issues/325
    [HttpGet("oid/r/{provider}")]
    public ActionResult OidPost(
        [FromRoute] string provider,
        [FromQuery] string state) // Although this is a GET function, this function is called `Post` for consistency with SAML
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            var options = new OidcClientOptions
            {
                Authority = config.OidEndpoint,
                ClientId = config.OidClientId,
                ClientSecret = config.OidSecret,
                RedirectUri = GetRequestBase() + "/sso/oid/r/" + provider,
                Scope = string.Join(" ", config.OidScopes.Prepend("openid profile")),
            };
            options.Policy.Discovery.ValidateEndpoints = false; // For Google and other providers with different endpoints
            var oidcClient = new OidcClient(options);
            var currentState = StateManager[state].State;
            var result = oidcClient.ProcessResponseAsync(Request.QueryString.Value, currentState).Result;
            if (result.IsError)
            {
                return ReturnError(StatusCodes.Status400BadRequest, result.Error + " Try logging in again.");
            }

            if (!config.EnableFolderRoles)
            {
                StateManager[state].Folders = new List<string>(config.EnabledFolders);
            }
            else
            {
                StateManager[state].Folders = new List<string>();
            }

            foreach (var claim in result.User.Claims)
            {
                if (claim.Type == "preferred_username")
                {
                    StateManager[state].Username = claim.Value;
                    if (config.Roles.Length == 0)
                    {
                        StateManager[state].Valid = true;
                    }
                }

                // Role processing
                // The regex matches any "." not preceded by a "\": a.b.c will be split into a, b, and c, but a.b\.c will be split into a, b.c (after processing the escaped dots)
                // We have to first process the RoleClaim string
                string[] segments = Regex.Split(config.RoleClaim, "(?<!\\\\)\\.");
                // Now we make sure that any escaped "."s ("\.") are replaced with "."
                for (int i = 0; i < segments.Length; i++)
                {
                    segments[i] = segments[i].Replace("\\.", ".");
                }

                if (claim.Type == segments[0])
                {
                    List<string> roles;
                    // If we are not using JSON values, just use the raw info from the claim value
                    if (segments.Length == 1)
                    {
                        roles = new List<string> { claim.Value };
                    }
                    else
                    {
                        // We recursively traverse through the JSON data for the roles and parse it
                        var json = JsonConvert.DeserializeObject<IDictionary<string, object>>(claim.Value);
                        for (int i = 1; i < segments.Length - 1; i++)
                        {
                            var segment = segments[i];
                            json = (json[segment] as JObject).ToObject<IDictionary<string, object>>();
                        }

                        // The final step is to take the JSON and turn it from a dictionary into a string
                        roles = (json[segments[^1]] as JArray).ToObject<List<string>>();
                    }

                    foreach (string role in roles)
                    {
                        // Check if allowed to login based on roles
                        if (config.Roles.Length != 0)
                        {
                            foreach (string validRoles in config.Roles)
                            {
                                if (role.Equals(validRoles))
                                {
                                    StateManager[state].Valid = true;
                                }
                            }
                        }

                        // Check if admin based on roles
                        if (config.AdminRoles.Length != 0)
                        {
                            foreach (string validAdminRoles in config.AdminRoles)
                            {
                                if (role.Equals(validAdminRoles))
                                {
                                    StateManager[state].Admin = true;
                                }
                            }
                        }

                        // Get allowed folders from roles
                        if (config.EnableFolderRoles)
                        {
                            foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping)
                            {
                                if (role.Equals(folderRoleMap.Role))
                                {
                                    StateManager[state].Folders.AddRange(folderRoleMap.Folders);
                                }
                            }
                        }
                    }
                }
            }

            // If the provider doesn't support preferred_username, then use sub
            if (!StateManager[state].Valid)
            {
                foreach (var claim in result.User.Claims)
                {
                    if (claim.Type == "sub")
                    {
                        StateManager[state].Username = claim.Value;
                        if (config.Roles.Length == 0)
                        {
                            StateManager[state].Valid = true;
                        }
                    }
                }
            }

            if (StateManager[state].Valid)
            {
                return Content(WebResponse.Generator(data: state, provider: provider, baseUrl: GetRequestBase(), mode: "OID"), MediaTypeNames.Text.Html);
            }
            else
            {
                _logger.LogWarning(
                    "OpenID user {Username} has one or more incorrect role claims: {@Claims}. Expected any one of: {@ExpectedClaims}",
                    StateManager[state].Username,
                    result.User.Claims.Select(o => new { o.Type, o.Value }),
                    config.Roles);

                return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
            }
        }

        // If the config doesn't have an active provider matching the requeset, show an error
        return BadRequest("No matching provider found");
    }

    /// <summary>
    /// Initiates the login flow for OpenID. This redirects the user to the auth provider.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <returns>An asynchronous result for the authentication.</returns>
    [HttpGet("oid/p/{provider}")]
    public async Task<ActionResult> OidChallenge(string provider)
    {
        Invalidate();
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException("Provider does not exist");
        }

        if (config.Enabled)
        {
            var options = new OidcClientOptions
            {
                Authority = config.OidEndpoint,
                ClientId = config.OidClientId,
                ClientSecret = config.OidSecret,
                RedirectUri = GetRequestBase() + "/sso/oid/r/" + provider,
                Scope = string.Join(" ", config.OidScopes.Prepend("openid profile")),
            };
            options.Policy.Discovery.ValidateEndpoints = false; // For Google and other providers with different endpoints
            var oidcClient = new OidcClient(options);
            var state = await oidcClient.PrepareLoginAsync().ConfigureAwait(false);
            StateManager.Add(state.State, new TimedAuthorizeState(state, DateTime.Now));
            return Redirect(state.StartUrl);
        }

        throw new ArgumentException("Provider does not exist");
    }

    /// <summary>
    /// Adds an OpenID auth configuration. Requires administrator privileges. If the provider already exists, it will be removed and readded.
    /// </summary>
    /// <param name="provider">The name of the provider to add.</param>
    /// <param name="config">The OID configuration (deserialized from a JSON post).</param>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("OID/Add/{provider}")]
    public void OidAdd(string provider, [FromBody] OidConfig config)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.OidConfigs[provider] = config;
        SSOPlugin.Instance.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Deletes an OpenID provider.
    /// </summary>
    /// <param name="provider">Name of provider to delete.</param>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("OID/Del/{provider}")]
    public void OidDel(string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.OidConfigs.Remove(provider);
        SSOPlugin.Instance.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Lists the OpenID providers configured. Requires administrator privileges.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("OID/Get")]
    public ActionResult OidProviders()
    {
        return Ok(SSOPlugin.Instance.Configuration.OidConfigs);
    }

    /// <summary>
    /// This is a debug endpoint to list all running OpenID flows. Requires administrator privileges.
    /// </summary>
    /// <returns>The list of OpenID flows in progress.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("OID/States")]
    public ActionResult OidStates()
    {
        return Ok(StateManager);
    }

    /// <summary>
    /// This endpoint accepts JSON and will authorize the user from the device values passed from the client.
    /// </summary>
    /// <param name="provider">Name of provider to authenticate against.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [HttpPost("OID/Auth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> OidAuth(string provider, [FromBody] AuthResponse response)
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            foreach (var kvp in StateManager)
            {
                if (kvp.Value.State.State.Equals(response.Data) && kvp.Value.Valid)
                {
                    var authenticationResult = await Authenticate(kvp.Value.Username, kvp.Value.Admin, config.EnableAuthorization, config.EnableAllFolders, kvp.Value.Folders.ToArray(), response, config.DefaultProvider)
                        .ConfigureAwait(false);
                    return Ok(authenticationResult);
                }
            }
        }

        return Problem("Something went wrong");
    }

    /// <summary>
    /// This is the callback for the SAML flow. This creates a webpage to complete auth.
    /// </summary>
    /// <param name="provider">The provider that is calling back.</param>
    /// <returns>A webpage that will complete the client-side flow.</returns>
    [HttpPost("SAML/p/{provider}")]
    public ActionResult SamlPost(string provider)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            var samlResponse = new Response(config.SamlCertificate, Request.Form["SAMLResponse"]);
            // If no roles are configured, don't use RBAC
            if (config.Roles.Length == 0)
            {
                return Content(WebResponse.Generator(data: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(samlResponse.Xml)), provider: provider, baseUrl: GetRequestBase(), mode: "SAML"), MediaTypeNames.Text.Html);
            }

            // Check if user is allowed to log in based on roles
            foreach (string role in samlResponse.GetCustomAttributes("Role"))
            {
                foreach (string allowedRole in config.Roles)
                {
                    if (allowedRole.Equals(role))
                    {
                        return Content(WebResponse.Generator(data: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(samlResponse.Xml)), provider: provider, baseUrl: GetRequestBase(), mode: "SAML"), MediaTypeNames.Text.Html);
                    }
                }
            }

            _logger.LogWarning(
                "SAML user: {UserId} has insufficient roles: {@Roles}. Expected any one of: {@ExpectedRoles}",
                samlResponse.GetNameID(),
                samlResponse.GetCustomAttributes("Role"),
                config.Roles);
            return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
        }

        return ReturnError(StatusCodes.Status400BadRequest, "No active providers found");
    }

    /// <summary>
    /// Initializes the SAML flow. This will redirect the user to the SAML provider.
    /// </summary>
    /// <param name="provider">The provider to being the flow with.</param>
    /// <returns>A redirect to the SAML provider's auth page.</returns>
    [HttpGet("SAML/p/{provider}")]
    public RedirectResult SamlChallenge(string provider)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException("Provider does not exist");
        }

        if (config.Enabled)
        {
            var request = new AuthRequest(
                config.SamlClientId,
                GetRequestBase() + "/sso/SAML/p/" + provider);

            return Redirect(request.GetRedirectUrl(config.SamlEndpoint));
        }

        throw new ArgumentException("Provider does not exist");
    }

    /// <summary>
    /// Adds a SAML configuration. If the provider already exists, overwrite it.
    /// </summary>
    /// <param name="provider">The provider name to add.</param>
    /// <param name="newConfig">The SAML configuration object (deserialized) from JSON.</param>
    /// <returns>The success result.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("SAML/Add/{provider}")]
    public OkResult SamlAdd(string provider, [FromBody] SamlConfig newConfig)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.SamlConfigs[provider] = newConfig;
        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Deletes a provider from the configuration with a given ID.
    /// </summary>
    /// <param name="provider">The ID of the provider to delete.</param>
    /// <returns>The success result.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SAML/Del/{provider}")]
    public OkResult SamlDel(string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.SamlConfigs.Remove(provider);
        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Returns a list of all SAML providers configured. Requires administrator privileges.
    /// </summary>
    /// <returns>A list of all of the Saml providers available.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("SAML/Get")]
    public ActionResult SamlProviders()
    {
        return Ok(SSOPlugin.Instance.Configuration.SamlConfigs);
    }

    /// <summary>
    /// This endpoint accepts JSON and will authorize the user from the device values passed from the client.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [HttpPost("SAML/Auth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> SamlAuth(string provider, [FromBody] AuthResponse response)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            bool isAdmin = false;
            var samlResponse = new Response(config.SamlCertificate, response.Data);
            List<string> folders;
            if (!config.EnableFolderRoles)
            {
                folders = new List<string>(config.EnabledFolders);
            }
            else
            {
                folders = new List<string>();
            }

            foreach (string role in samlResponse.GetCustomAttributes("Role"))
            {
                foreach (string allowedRole in config.AdminRoles)
                {
                    if (allowedRole.Equals(role))
                    {
                        isAdmin = true;
                    }
                }

                if (config.EnableFolderRoles)
                {
                    foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping)
                    {
                        if (folderRoleMap.Role.Equals(role))
                        {
                            folders.AddRange(folderRoleMap.Folders);
                        }
                    }
                }
            }

            var authenticationResult = await Authenticate(samlResponse.GetNameID(), isAdmin, config.EnableAuthorization, config.EnableAllFolders, folders.ToArray(), response, config.DefaultProvider)
                .ConfigureAwait(false);
            return Ok(authenticationResult);
        }

        return Problem("Something went wrong");
    }

    /// <summary>
    /// Removes a user from SSO auth and switches it back to another auth provider. Requires administrator privileges.
    /// </summary>
    /// <param name="username">The username to switch to the new provider.</param>
    /// <param name="provider">The new provider to switch to.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("Unregister/{username}")]
    public ActionResult Unregister(string username, [FromBody] string provider)
    {
        User user = _userManager.GetUserByName(username);
        user.AuthenticationProviderId = provider;

        return Ok();
    }

    /// <summary>
    /// Authenticates the user with the given information.
    /// </summary>
    /// <param name="username">The username of the user to authenticate.</param>
    /// <param name="isAdmin">Determines whether this user is an administrator.</param>
    /// <param name="enableAuthorization">Determines whether RBAC is used for this user.</param>
    /// <param name="enableAllFolders">Determines whether all folders are enabled.</param>
    /// <param name="enabledFolders">Determines which folders should be enabled for this client.</param>
    /// <param name="authResponse">The client information to authenticate the user with.</param>
    /// <param name="defaultProvider">The default provider of the user to be set after logging in.</param>
    private async Task<AuthenticationResult> Authenticate(string username, bool isAdmin, bool enableAuthorization, bool enableAllFolders, string[] enabledFolders, AuthResponse authResponse, string defaultProvider)
    {
        User user = null;
        user = _userManager.GetUserByName(username);

        if (user == null)
        {
            _logger.LogInformation("SSO user doesn't exist, creating...");
            user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
            user.AuthenticationProviderId = GetType().FullName;
        }

        if (enableAuthorization)
        {
            user.SetPermission(PermissionKind.IsAdministrator, isAdmin);
            user.SetPermission(PermissionKind.EnableAllFolders, enableAllFolders);
            if (!enableAllFolders)
            {
                user.SetPreference(PreferenceKind.EnabledFolders, enabledFolders);
            }
        }

        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        var authRequest = new AuthenticationRequest();
        authRequest.UserId = user.Id;
        authRequest.Username = user.Username;
        authRequest.App = authResponse.AppName;
        authRequest.AppVersion = authResponse.AppVersion;
        authRequest.DeviceId = authResponse.DeviceID;
        authRequest.DeviceName = authResponse.DeviceName;
        _logger.LogInformation("Auth request created...");
        if (!string.IsNullOrEmpty(defaultProvider))
        {
            user.AuthenticationProviderId = defaultProvider;
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            _logger.LogInformation("Set default login provider to " + defaultProvider);
        }

        return await _sessionManager.AuthenticateDirect(authRequest).ConfigureAwait(false);
    }

    private void Invalidate()
    {
        foreach (var kvp in StateManager)
        {
            var now = DateTime.Now;
            if (now.Subtract(kvp.Value.Created).TotalMinutes > 1)
            {
                StateManager.Remove(kvp.Key);
            }
        }
    }

    private string GetRequestBase()
    {
        return Request.Scheme + "://" + Request.Host + Request.PathBase;
    }

    private ContentResult ReturnError(int code, string message)
    {
        var errorResult = new ContentResult();
        errorResult.Content = message;
        errorResult.ContentType = MediaTypeNames.Text.Plain;
        errorResult.StatusCode = code;
        return errorResult;
    }
}

/// <summary>
/// The data the client should pass back to the API.
/// </summary>
public class AuthResponse
{
    /// <summary>
    /// Gets or sets the device ID of the client.
    /// </summary>
    public string DeviceID { get; set; }

    /// <summary>
    /// Gets or sets the device name of the client.
    /// </summary>
    public string DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the app name of the client.
    /// </summary>
    public string AppName { get; set; }

    /// <summary>
    /// Gets or sets the app version of the client.
    /// </summary>
    public string AppVersion { get; set; }

    /// <summary>
    /// Gets or sets the auth data of the client (for authorizing the response).
    /// </summary>
    public string Data { get; set; }
}

/// <summary>
/// A manager for OpenID to manage the state of the clients.
/// </summary>
public class TimedAuthorizeState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimedAuthorizeState"/> class.
    /// </summary>
    /// <param name="state">The AuthorizeState to time.</param>
    /// <param name="created">When this state was created.</param>
    public TimedAuthorizeState(AuthorizeState state, DateTime created)
    {
        State = state;
        Created = created;
        Valid = false;
        Admin = false;
    }

    /// <summary>
    /// Gets or sets the Authorization State of the client.
    /// </summary>
    public AuthorizeState State { get; set; }

    /// <summary>
    /// Gets or sets when this object was created to time it out.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is valid.
    /// </summary>
    public bool Valid { get; set; }

    /// <summary>
    /// Gets or sets the user tied to the state.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is an administrator.
    /// </summary>
    public bool Admin { get; set; }

    /// <summary>
    /// Gets or sets the folders the user is allowed access to.
    /// </summary>
    public List<string> Folders { get; set; }
}
