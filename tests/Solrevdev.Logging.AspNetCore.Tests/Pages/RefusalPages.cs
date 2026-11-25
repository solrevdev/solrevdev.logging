using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Solrevdev.Logging.AspNetCore.Tests.Pages;

/// <summary>
/// What a test wants the form page's handler to do. A singleton the test host registers, so
/// one page serves every case.
/// </summary>
public sealed class FormScript
{
    public Func<FormModel, IActionResult> Run { get; set; } = page => page.Page();
}

public sealed class FormInput
{
    public int Quantity { get; set; }

    public string? PostalCode { get; set; }
}

/// <summary>
/// A form page with an unnamed and a named handler. Anti-forgery is off here so these tests
/// are about the form filter alone; <see cref="GuardedModel"/> keeps the check.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class FormModel(FormScript script) : PageModel
{
    [BindProperty]
    public FormInput Input { get; set; } = new();

    public IActionResult OnPost() => script.Run(this);

    public IActionResult OnPostSave() => script.Run(this);
}

/// <summary>
/// A page with the framework's default anti-forgery check on its posts.
/// </summary>
public sealed class GuardedModel : PageModel
{
    public bool Reached { get; private set; }

    public IActionResult OnPost()
    {
        Reached = true;
        return Page();
    }
}
