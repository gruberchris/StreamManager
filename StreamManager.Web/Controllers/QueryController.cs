using Microsoft.AspNetCore.Mvc;

namespace StreamManager.Web.Controllers;

public class QueryController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}