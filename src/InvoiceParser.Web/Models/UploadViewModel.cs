using Microsoft.AspNetCore.Mvc.Rendering;

namespace InvoiceParser.Web.Models;

public class UploadViewModel
{
    public int CustomerId { get; set; }
    public int CarrierId { get; set; }
    public IFormFile? PdfFile { get; set; }

    public List<SelectListItem> Customers { get; set; } = new();
    public List<SelectListItem> Carriers { get; set; } = new();
}
