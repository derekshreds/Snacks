using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     REST surface for the named encoder policies (preset bundles of
///     <see cref="EncoderOptions"/>). Mirrors the read/save patterns used by
///     <see cref="SettingsController"/> — atomic persistence is in
///     <see cref="PolicyService"/>; the controller is a thin transport layer.
/// </summary>
[Route("api/policies")]
[ApiController]
public sealed class PoliciesController : ControllerBase
{
    private readonly PolicyService               _policies;
    private readonly SettingsPersistenceService  _settingsPersistence;
    private readonly ILogger<PoliciesController>? _log;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    public PoliciesController(
        PolicyService policies,
        SettingsPersistenceService settingsPersistence,
        ILogger<PoliciesController>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(settingsPersistence);
        _policies            = policies;
        _settingsPersistence = settingsPersistence;
        _log                 = logger;
    }

    /// <summary> Returns every policy known to the system. </summary>
    [HttpGet]
    public IActionResult List() => new JsonResult(_policies.List());

    /// <summary>
    ///     Returns which policy (if any) the current <c>settings.json</c> matches and
    ///     whether the user has tweaked anything since applying it. Drives the "Active
    ///     Policy" card at the top of the Policies tab and the per-card highlight in
    ///     the policy grid.
    /// </summary>
    [HttpGet("active")]
    public IActionResult Active()
    {
        var current = _settingsPersistence.Load() ?? new Models.EncoderOptions();
        return new JsonResult(_policies.GetActive(current));
    }

    /// <summary> Returns a single policy by Id, or 404. </summary>
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var policy = _policies.Get(id);
        return policy != null ? new JsonResult(policy) : NotFound();
    }

    /// <summary>
    ///     Creates a new custom policy. The server assigns the Id and ignores any
    ///     <c>BuiltIn</c> flag from the client.
    /// </summary>
    [HttpPost]
    public IActionResult Create([FromBody] PolicyCreateRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");
        if (request.Options == null)
            return BadRequest("Options are required.");

        var created = _policies.Create(request.Name, request.Description, request.OutcomeBullets, request.Options);

        // "Save current as policy" is the only flow that calls Create today, and in that
        // flow the snapshot equals the current settings.json. Pin the new policy as
        // active so the user sees "Active: <new name>" immediately after saving instead
        // of "Custom" (the previous state, before the snapshot existed as a policy).
        _policies.SetLastAppliedPolicyId(created.Id);

        return new JsonResult(created);
    }

    /// <summary> Updates a custom policy. Built-ins return 400 with an explanatory message. </summary>
    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] PolicyCreateRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");
        if (request.Options == null)
            return BadRequest("Options are required.");

        try
        {
            var updated = _policies.Update(id, request.Name, request.Description, request.OutcomeBullets, request.Options);
            return updated != null ? new JsonResult(updated) : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary> Deletes a custom policy. Built-ins return 400. </summary>
    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        try
        {
            return _policies.Delete(id)
                ? new JsonResult(new { success = true })
                : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    ///     Duplicates an existing policy (typically a built-in) into a new custom one
    ///     with the name <c>"{original} (custom)"</c>. Returns the newly-created policy.
    /// </summary>
    [HttpPost("{id}/duplicate")]
    public IActionResult Duplicate(string id)
    {
        var dup = _policies.Duplicate(id);
        return dup != null ? new JsonResult(dup) : NotFound();
    }

    /// <summary>
    ///     Applies a policy: copies its <see cref="Policy.Options"/> into the persisted
    ///     <c>settings.json</c> and into the in-memory transcoding options. Uses the
    ///     same write path as the settings POST endpoint so migration + activation logic
    ///     can't drift between the two.
    /// </summary>
    [HttpPost("{id}/apply")]
    public IActionResult Apply(string id)
    {
        var policy = _policies.Get(id);
        if (policy == null) return NotFound();

        try
        {
            // Policies never carry filesystem paths (those are machine-local). Pull
            // the user's existing OutputDirectory + EncodeDirectory off the current
            // settings and graft them onto the policy's options before persisting -
            // otherwise switching policies would silently nuke paths the user
            // configured.
            var toApply = policy.Options.Clone();
            var current = _settingsPersistence.Load();
            if (current != null) toApply.MergeMachineLocalFrom(current);

            _settingsPersistence.PersistAndActivate(toApply);
            _policies.SetLastAppliedPolicyId(policy.Id);
            _log?.LogInformation("PolicyApplied id={Id} name={Name}", policy.Id, policy.Name);
            return new JsonResult(new { success = true, applied = policy.Name });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    ///     Downloads a single policy as a <see cref="PolicyDocument"/> with one entry.
    ///     The browser saves it to disk via <c>Content-Disposition</c>.
    /// </summary>
    [HttpGet("{id}/export")]
    public IActionResult ExportOne(string id)
    {
        var doc = _policies.ExportOne(id);
        if (doc == null) return NotFound();

        var bytes    = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc, _jsonOptions));
        var fileName = SafeFileName(doc.Policies[0].Name) + ".snackspolicy.json";
        return File(bytes, "application/json", fileName);
    }

    /// <summary>
    ///     Downloads every custom policy as a single <see cref="PolicyDocument"/>.
    ///     Built-ins are deliberately excluded — see <see cref="PolicyService.ExportAllCustom"/>.
    /// </summary>
    [HttpGet("export-all")]
    public IActionResult ExportAll()
    {
        var doc   = _policies.ExportAllCustom();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc, _jsonOptions));
        return File(bytes, "application/json", "snacks-policies.snackspolicies.json");
    }

    /// <summary>
    ///     Imports the supplied <see cref="PolicyDocument"/>. Every entry becomes a new
    ///     custom policy (fresh Id, <c>BuiltIn=false</c>, renamed on collision). Returns
    ///     the list of imported policies. 400 on schema-version or shape errors.
    /// </summary>
    [HttpPost("import")]
    public IActionResult Import([FromBody] PolicyDocument? document)
    {
        if (document == null) return BadRequest("Empty or unparseable import body.");

        try
        {
            var added = _policies.Import(document);
            return new JsonResult(new { success = true, count = added.Count, policies = added });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    ///     Replaces filename-unsafe characters with underscore so a policy named
    ///     <c>"My / Policy?"</c> serializes to <c>"My _ Policy_"</c> in the
    ///     <c>Content-Disposition</c> header rather than tripping browsers.
    /// </summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb      = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    /// <summary>
    ///     Request body for create + update endpoints. Defined as a record-style class
    ///     rather than a record so the model-binder honors property defaults.
    /// </summary>
    public sealed class PolicyCreateRequest
    {
        public string?         Name           { get; set; }
        public string?         Description    { get; set; }
        public List<string>?   OutcomeBullets { get; set; }
        public EncoderOptions? Options        { get; set; }
    }
}
