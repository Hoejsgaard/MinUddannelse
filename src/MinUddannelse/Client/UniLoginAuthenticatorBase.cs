using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using MinUddannelse.Configuration;

namespace MinUddannelse.Client;

/// <summary>
/// Base authenticator for UniLogin SAML-based authentication
/// Handles the multi-step form submission flow with detailed logging
/// </summary>
public abstract class UniLoginAuthenticatorBase : IDisposable
{
    // Authentication flow constants
    private const int MaxAuthenticationSteps = 10;
    private const int AuthenticationStepThreshold = 5;
    private const int ScriptPreviewLength = 200;

    private bool _disposed = false;
    private readonly ILogger _logger;
    private readonly string _loginUrl;
    private readonly string _password;
    private readonly string _username;
    private readonly string _apiBaseUrl;
    private readonly string _studentDataPath;
    private readonly IHttpClientFactory _httpClientFactory;
    private bool _loggedIn;

    public UniLoginAuthenticatorBase(
        IHttpClientFactory httpClientFactory,
        string username,
        string password,
        string loginUrl,
        string successUrl,
        ILogger logger,
        string apiBaseUrl = "https://www.minuddannelse.net",
        string studentDataPath = "/api/stamdata/elev/getElev")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _username = username ?? throw new ArgumentNullException(nameof(username));
        _password = password ?? throw new ArgumentNullException(nameof(password));
        _loginUrl = loginUrl;
        SuccessUrl = successUrl;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _apiBaseUrl = apiBaseUrl;
        _studentDataPath = studentDataPath;
    }

    private readonly CookieContainer _cookieContainer = new CookieContainer();

    protected HttpClient CreateHttpClient()
    {
        // Create HTTP client with shared cookie container for this child instance
        // This prevents session contamination between children while maintaining
        // cookie sharing within a single child's authentication session
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = _cookieContainer, // Shared container for this child instance
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.Timeout = TimeSpan.FromMinutes(2);

        return client;
    }
    protected string SuccessUrl { get; }

    public async Task<bool> LoginAsync()
    {
        _logger.LogDebug("Starting login process for user: {Username}", _username);
        _logger.LogDebug("Initial URL: {LoginUrl}", _loginUrl);

        using var httpClient = CreateHttpClient();
        var response = await httpClient.GetAsync(_loginUrl);
        var content = await response.Content.ReadAsStringAsync();

        _logger.LogDebug("Initial response status: {StatusCode}", response.StatusCode);
        _logger.LogDebug("Response URL: {ResponseUri}", response.RequestMessage?.RequestUri);

        return await ProcessLoginResponseAsync(httpClient, content, response);
    }

    private async Task<bool> ProcessLoginResponseAsync(HttpClient httpClient, string content, HttpResponseMessage initialResponse)
    {
        var authState = InitializeAuthenticationState(initialResponse);

        for (var stepCounter = 0; stepCounter < MaxAuthenticationSteps; stepCounter++)
        {
            LogAuthenticationStep(stepCounter, authState.CurrentUrl, content);

            authState.HasSubmittedCredentials = UpdateCredentialsSubmissionStatus(authState.CurrentUrl, content, authState.HasSubmittedCredentials);

            if (await TryVerifyAuthenticationAfterCredentials(httpClient, authState.CurrentUrl, authState.HasSubmittedCredentials))
            {
                return true;
            }

            var stepResult = await ProcessAuthenticationStep(httpClient, content, authState.CurrentUrl, authState.HasSubmittedCredentials, stepCounter);

            if (stepResult.IsAuthenticated)
            {
                return true;
            }

            if (stepResult.ShouldContinue)
            {
                authState.CurrentUrl = stepResult.NewUrl;
                content = stepResult.NewContent;
                authState.HasSubmittedCredentials = stepResult.NewHasSubmittedCredentials;
                continue;
            }

            if (stepResult.ShouldBreak)
            {
                break;
            }
        }

        _logger.LogWarning("Login failed after {MaxSteps} steps", MaxAuthenticationSteps);
        return false;
    }

    private AuthenticationState InitializeAuthenticationState(HttpResponseMessage initialResponse)
    {
        return new AuthenticationState
        {
            CurrentUrl = initialResponse.RequestMessage?.RequestUri?.ToString() ?? _loginUrl,
            HasSubmittedCredentials = false
        };
    }

    private void LogAuthenticationStep(int stepCounter, string currentUrl, string content)
    {
        _logger.LogDebug("===== STEP {StepCounter} =====", stepCounter);
        _logger.LogDebug("Current URL: {CurrentUrl}", currentUrl);
        _logger.LogDebug("Content length: {ContentLength} chars", content.Length);
    }

    private bool UpdateCredentialsSubmissionStatus(string currentUrl, string content, bool hasSubmittedCredentials)
    {
        if (currentUrl.Contains("broker.unilogin.dk") && content.Contains("password"))
        {
            _logger.LogDebug("Credentials form detected");
            return true;
        }
        return hasSubmittedCredentials;
    }

    private async Task<AuthenticationStepResult> ProcessAuthenticationStep(HttpClient httpClient, string content, string currentUrl, bool hasSubmittedCredentials, int stepCounter)
    {
        try
        {
            var doc = CreateHtmlDocument(content);
            LogPageInformation(doc);

            if (CheckForAuthenticationSuccess(doc, currentUrl, stepCounter))
            {
                return AuthenticationStepResult.Authenticated();
            }

            var (authenticated, newHasSubmittedCredentials, newContent, newUrl) =
                await TrySubmitForm(httpClient, content, currentUrl, hasSubmittedCredentials);

            if (authenticated)
            {
                return AuthenticationStepResult.Authenticated();
            }

            if (newContent != content || newUrl != currentUrl)
            {
                return AuthenticationStepResult.Continue(newContent, newUrl, newHasSubmittedCredentials);
            }

            var (navigated, navContent, navUrl) = await TryAlternativeNavigation(httpClient, doc, currentUrl);
            if (navigated)
            {
                return AuthenticationStepResult.Continue(navContent, navUrl, hasSubmittedCredentials);
            }

            LogErrorMessages(doc);
            return AuthenticationStepResult.Break();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Step {StepCounter} failed with exception", stepCounter);
            return AuthenticationStepResult.Break();
        }
    }

    private HtmlDocument CreateHtmlDocument(string content)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(content);
        return doc;
    }

    private void LogPageInformation(HtmlDocument doc)
    {
        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
        _logger.LogDebug("Page title: {Title}", title ?? "No title found");
        LogFormStructure(doc);
    }

    private sealed class AuthenticationState
    {
        public string CurrentUrl { get; set; } = string.Empty;
        public bool HasSubmittedCredentials { get; set; }
    }

    private sealed class AuthenticationStepResult
    {
        public bool IsAuthenticated { get; private set; }
        public bool ShouldContinue { get; private set; }
        public bool ShouldBreak { get; private set; }
        public string NewContent { get; private set; } = string.Empty;
        public string NewUrl { get; private set; } = string.Empty;
        public bool NewHasSubmittedCredentials { get; private set; }

        public static AuthenticationStepResult Authenticated() => new() { IsAuthenticated = true };
        public static AuthenticationStepResult Continue(string content, string url, bool hasSubmittedCredentials) =>
            new() { ShouldContinue = true, NewContent = content, NewUrl = url, NewHasSubmittedCredentials = hasSubmittedCredentials };
        public static AuthenticationStepResult Break() => new() { ShouldBreak = true };
    }

    private void LogFormStructure(HtmlDocument doc)
    {
        var forms = doc.DocumentNode.SelectNodes("//form");
        if (forms != null)
        {
            _logger.LogDebug("Found {FormCount} form(s) on page", forms.Count);
            foreach (var form in forms)
            {
                var formAction = form.Attributes["action"]?.Value;
                var formMethod = form.Attributes["method"]?.Value;
                var formId = form.Attributes["id"]?.Value;
                var formName = form.Attributes["name"]?.Value;
                _logger.LogDebug("Form: action='{FormAction}', method='{FormMethod}', id='{FormId}', name='{FormName}'",
                    formAction, formMethod, formId, formName);

                var inputs = form.SelectNodes(".//input");
                if (inputs != null)
                {
                    foreach (var input in inputs)
                    {
                        var inputName = input.Attributes["name"]?.Value;
                        var inputType = input.Attributes["type"]?.Value;
                        var inputId = input.Attributes["id"]?.Value;
                        if (inputType != "hidden")
                        {
                            _logger.LogDebug("Input: name='{InputName}', type='{InputType}', id='{InputId}'",
                                inputName, inputType, inputId);
                        }
                    }
                }
            }
        }
        else
        {
            _logger.LogDebug("No forms found on this page");
            LogJavaScriptRedirects(doc);
            LogLoginLinks(doc);
            LogMetaRefresh(doc);
        }
    }

    private void LogJavaScriptRedirects(HtmlDocument doc)
    {
        var scripts = doc.DocumentNode.SelectNodes("//script");
        if (scripts != null)
        {
            foreach (var script in scripts)
            {
                if (script.InnerText.Contains("window.location") ||
                    script.InnerText.Contains("redirect") ||
                    script.InnerText.Contains("submit"))
                {
                    _logger.LogDebug("Found potential JavaScript redirect/submit");
                    var scriptPreview = script.InnerText.Length > ScriptPreviewLength
                        ? script.InnerText.Substring(0, ScriptPreviewLength) + "..."
                        : script.InnerText;
                    _logger.LogDebug("Script preview: {ScriptPreview}", scriptPreview);
                }
            }
        }
    }

    private void LogLoginLinks(HtmlDocument doc)
    {
        var links = doc.DocumentNode.SelectNodes("//a[contains(@href, 'login') or contains(@href, 'Login') or contains(@href, 'auth')]");
        if (links != null)
        {
            _logger.LogDebug("Found {LinkCount} login-related link(s)", links.Count);
            foreach (var link in links)
            {
                var href = link.Attributes["href"]?.Value;
                var text = link.InnerText?.Trim();
                _logger.LogDebug("Link: href='{Href}', text='{Text}'", href, text);
            }
        }
    }

    private void LogMetaRefresh(HtmlDocument doc)
    {
        var metaRefresh = doc.DocumentNode.SelectSingleNode("//meta[@http-equiv='refresh']");
        if (metaRefresh != null)
        {
            var refreshContent = metaRefresh.Attributes["content"]?.Value;
            _logger.LogDebug("Meta refresh found: {RefreshContent}", refreshContent);
        }
    }

    private void LogErrorMessages(HtmlDocument doc)
    {
        var errorElements = doc.DocumentNode.SelectNodes("//*[contains(@class, 'error') or contains(@class, 'alert')]");
        if (errorElements != null)
        {
            foreach (var error in errorElements)
            {
                _logger.LogWarning("Possible error message: {ErrorMessage}", error.InnerText?.Trim());
            }
        }

        var loginButton = doc.DocumentNode.SelectSingleNode("//button[contains(text(), 'Log') or contains(text(), 'Uni')]");
        if (loginButton != null)
        {
            _logger.LogDebug("Found login button: {ButtonText}", loginButton.InnerText);
        }
    }

    private Tuple<string, Dictionary<string, string>>? ExtractFormData(string htmlContent)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(htmlContent);

            var formNode = doc.DocumentNode.SelectSingleNode("//form[contains(@action, 'login') or contains(@action, 'Login') or contains(@action, 'auth')]")
                          ?? doc.DocumentNode.SelectSingleNode("//form");

            if (formNode == null)
            {
                _logger.LogDebug("No form found for data extraction");
                return null;
            }

            var actionUrl = formNode.Attributes["action"]?.Value;
            if (actionUrl == null)
            {
                _logger.LogDebug("Form has no action attribute");
                return null;
            }

            var formData = BuildFormData(doc, formNode);
            var writer = new StringWriter();
            HttpUtility.HtmlDecode(actionUrl, writer);
            var decodedUrl = writer.ToString();

            if (!decodedUrl.StartsWith("http"))
            {
                var baseUri = new Uri(_loginUrl);
                var absoluteUri = new Uri(baseUri, decodedUrl);
                decodedUrl = absoluteUri.ToString();
            }

            return new Tuple<string, Dictionary<string, string>>(decodedUrl, formData);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExtractFormData exception");
            return null;
        }
    }

    private Dictionary<string, string> BuildFormData(HtmlDocument document, HtmlNode formNode)
    {
        var formData = InitializeFormData(formNode);
        if (formData == null) return new Dictionary<string, string> { { "selectedIdp", "uni_idp" } };

        ProcessInputElements(formNode, formData);
        ProcessSelectElements(formNode, formData);

        return formData;
    }

    private Dictionary<string, string>? InitializeFormData(HtmlNode formNode)
    {
        var inputs = formNode.SelectNodes(".//input");
        if (inputs == null)
        {
            _logger.LogDebug("No inputs found in form");
            return null;
        }

        return new Dictionary<string, string>();
    }

    private void ProcessInputElements(HtmlNode formNode, Dictionary<string, string> formData)
    {
        var inputs = formNode.SelectNodes(".//input");
        if (inputs == null) return;

        foreach (var input in inputs)
        {
            var inputData = ExtractInputData(input);
            if (inputData == null) continue;

            LogFormFieldAddition(inputData.Name, inputData.Value, inputData.Type);
            var finalValue = DetermineInputValue(inputData);
            formData[inputData.Name] = finalValue;
            LogCredentialFieldAssignment(inputData.Name, finalValue);
        }
    }

    private void ProcessSelectElements(HtmlNode formNode, Dictionary<string, string> formData)
    {
        var selects = formNode.SelectNodes(".//select");
        if (selects == null) return;

        foreach (var select in selects)
        {
            var name = select.Attributes["name"]?.Value;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var selectedValue = ExtractSelectedValue(select);
            if (selectedValue != null)
            {
                formData[name] = selectedValue;
                _logger.LogDebug("Adding select to form: {FieldName} = {Value}", name, selectedValue);
            }
        }
    }

    private InputFieldData? ExtractInputData(HtmlNode input)
    {
        var name = input.Attributes["name"]?.Value;
        if (string.IsNullOrWhiteSpace(name)) return null;

        return new InputFieldData
        {
            Name = name,
            Value = input.GetAttributeValue("value", string.Empty),
            Type = input.GetAttributeValue("type", "text")
        };
    }

    private void LogFormFieldAddition(string name, string value, string type)
    {
        if (type != "hidden")
        {
            var displayValue = name.Contains("password", StringComparison.OrdinalIgnoreCase) ? "***" : value;
            _logger.LogDebug("Adding to form: {FieldName} = {Value}", name, displayValue);
        }
    }

    private string DetermineInputValue(InputFieldData inputData)
    {
        var lowerName = inputData.Name.ToLowerInvariant();
        return lowerName switch
        {
            "username" or "j_username" or "user" or "login" => _username,
            "password" or "j_password" or "pass" or "pwd" => _password,
            _ => inputData.Value
        };
    }

    private void LogCredentialFieldAssignment(string fieldName, string value)
    {
        var lowerName = fieldName.ToLowerInvariant();
        if (lowerName is "username" or "j_username" or "user" or "login")
        {
            _logger.LogDebug("Setting username field '{FieldName}' to: {Username}", fieldName, value);
        }
        else if (lowerName is "password" or "j_password" or "pass" or "pwd")
        {
            _logger.LogDebug("Setting password field '{FieldName}' to: ***", fieldName);
        }
    }

    private string? ExtractSelectedValue(HtmlNode select)
    {
        var selectedOption = select.SelectSingleNode(".//option[@selected]")
                          ?? select.SelectSingleNode(".//option");
        return selectedOption?.Attributes["value"]?.Value ?? "";
    }

    private sealed class InputFieldData
    {
        public string Name { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
    }

    private bool CheckIfLoginSuccessful(HttpResponseMessage response)
    {
        _loggedIn = response.RequestMessage?.RequestUri?.ToString().Contains(SuccessUrl.Replace("https://", "").Replace("http://", "")) ?? false;

        if (_loggedIn)
        {
            _logger.LogDebug("Login check: SUCCESS - URL matches success pattern");
        }
        else
        {
            _logger.LogDebug("Login check: Current URL doesn't match success URL");
        }

        return _loggedIn;
    }

    private async Task<bool> VerifyAuthentication(HttpClient httpClient)
    {
        _logger.LogDebug("Verifying authentication status");

        var endpoints = new[]
        {
            $"{_apiBaseUrl}{_studentDataPath}",
            $"{_apiBaseUrl}/api/stamdata/getProfiles",
            $"{_apiBaseUrl}/api/stamdata/bruger/getBruger"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                var url = endpoint + "?_=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _logger.LogDebug("Testing endpoint: {Endpoint}", endpoint);

                var response = await httpClient.GetAsync(url);
                _logger.LogDebug("Response: {StatusCode}", response.StatusCode);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.LogDebug("Content length: {ContentLength} chars", content.Length);

                    if (content.StartsWith('{') || content.StartsWith('['))
                    {
                        _logger.LogDebug("Valid JSON response received");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error testing endpoint");
            }
        }

        _logger.LogWarning("All authentication verification endpoints failed");
        return false;
    }

    private async Task<bool> TryVerifyAuthenticationAfterCredentials(HttpClient httpClient, string currentUrl, bool hasSubmittedCredentials)
    {
        if (!hasSubmittedCredentials || !currentUrl.Contains("minuddannelse.net") || currentUrl.Contains("Login"))
        {
            return false;
        }

        _logger.LogDebug("Back at MinUddannelse after credential submission");
        var authenticated = await VerifyAuthentication(httpClient);
        if (authenticated)
        {
            _logger.LogInformation("Authentication confirmed via API access");
            return true;
        }

        _logger.LogWarning("At MinUddannelse but API access failed - may need another redirect");
        return false;
    }

    private bool CheckForAuthenticationSuccess(HtmlDocument doc, string currentUrl, int stepCounter)
    {
        if (!currentUrl.Contains("/Node/") && !currentUrl.Contains("/portal/") &&
            !(currentUrl.Contains("minuddannelse.net") && !currentUrl.Contains("Login") && !currentUrl.Contains("Forside")))
        {
            return false;
        }

        _logger.LogDebug("Possible success - URL indicates we're back at MinUddannelse");
        _logger.LogDebug("Checking for user profile");

        var userInfo = doc.DocumentNode.SelectSingleNode("//*[contains(@class, 'user') or contains(@class, 'profile')]");
        if (userInfo != null)
        {
            _logger.LogDebug("Found user info element");
        }

        var scripts = doc.DocumentNode.SelectNodes("//script");
        if (scripts != null)
        {
            foreach (var script in scripts)
            {
                if (script.InnerText.Contains("currentUser") ||
                    script.InnerText.Contains("bruger") ||
                    script.InnerText.Contains("elev"))
                {
                    _logger.LogInformation("Found user context in script - authentication successful");
                    return true;
                }
            }
        }

        if (stepCounter > AuthenticationStepThreshold && currentUrl.Contains("minuddannelse.net"))
        {
            _logger.LogInformation("After multiple redirects, back at MinUddannelse - assuming success");
            return true;
        }

        return false;
    }

    private async Task<(bool authenticated, bool hasSubmittedCredentials, string newContent, string newUrl)> TrySubmitForm(
        HttpClient httpClient, string content, string currentUrl, bool hasSubmittedCredentials)
    {
        var formData = ExtractFormData(content);
        if (formData == null)
        {
            return (false, hasSubmittedCredentials, content, currentUrl);
        }

        _logger.LogDebug("Submitting form to: {FormUrl}", formData.Item1);
        _logger.LogDebug("Form data fields: {FormFields}", string.Join(", ", formData.Item2.Keys));

        var isCredentialForm = formData.Item2.ContainsKey("username") ||
                              formData.Item2.ContainsKey("Username") ||
                              formData.Item2.ContainsKey("j_username");

        if (isCredentialForm)
        {
            _logger.LogDebug("Submitting credentials for user: {Username}", _username);
            hasSubmittedCredentials = true;
        }

        var response = await httpClient.PostAsync(formData.Item1, new FormUrlEncodedContent(formData.Item2));
        var newContent = await response.Content.ReadAsStringAsync();
        var newUrl = response.RequestMessage?.RequestUri?.ToString() ?? currentUrl;

        _logger.LogDebug("Response status: {StatusCode}", response.StatusCode);
        _logger.LogDebug("New URL: {CurrentUrl}", newUrl);

        if (hasSubmittedCredentials && newUrl.Contains("minuddannelse.net"))
        {
            _logger.LogDebug("Returned to MinUddannelse after credentials, verifying");
            var authenticated = await VerifyAuthentication(httpClient);
            if (authenticated)
            {
                _logger.LogInformation("Login successful");
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return (true, hasSubmittedCredentials, newContent, newUrl);
            }
        }

        var success = CheckIfLoginSuccessful(response);
        if (success)
        {
            _logger.LogInformation("Login successful (URL match)");
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return (true, hasSubmittedCredentials, newContent, newUrl);
        }

        return (false, hasSubmittedCredentials, newContent, newUrl);
    }

    private async Task<(bool navigated, string newContent, string newUrl)> TryAlternativeNavigation(
        HttpClient httpClient, HtmlDocument doc, string currentUrl)
    {
        var uniLoginLink = doc.DocumentNode.SelectSingleNode("//a[contains(@href, 'unilogin-idp-prod')]");
        if (uniLoginLink == null)
        {
            return (false, string.Empty, currentUrl);
        }

        var href = uniLoginLink.Attributes["href"]?.Value;
        _logger.LogDebug("Found UniLogin link, navigating to: {Href}", href);

        if (!string.IsNullOrEmpty(href))
        {
            if (!href.StartsWith("http"))
            {
                var baseUri = new Uri(currentUrl);
                var absoluteUri = new Uri(baseUri, href);
                href = absoluteUri.ToString();
            }

            _logger.LogDebug("Following UniLogin link to: {Href}", href);
            var response = await httpClient.GetAsync(href);
            var newContent = await response.Content.ReadAsStringAsync();
            var newUrl = response.RequestMessage?.RequestUri?.ToString() ?? href;

            _logger.LogDebug("After following link - Status: {StatusCode}", response.StatusCode);
            _logger.LogDebug("New URL: {CurrentUrl}", newUrl);

            return (true, newContent, newUrl);
        }

        return (false, string.Empty, currentUrl);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
