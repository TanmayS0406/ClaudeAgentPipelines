using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeAgentPipelines.Controllers;

public sealed class HomeController : Controller
{
    [AllowAnonymous]
    public IActionResult Error() => View();
}
