using Microsoft.AspNetCore.Mvc;

namespace SalonCrm.Api.Features.Common;

// User-facing (Slovak) texts of problem responses, kept in one place.
public static class Messages
{
    public const string GdprRequired = "Na rezerváciu je potrebný súhlas so spracovaním osobných údajov.";
    public const string SlotTaken = "Vybraný termín už nie je voľný.";
    public const string InvalidTime = "Neplatný čas termínu.";
    public const string AppointmentWrongState = "Termín v tomto stave už nie je možné zmeniť.";
    public const string AppointmentNotStarted = "Termín ešte nezačal.";
    public const string AppointmentAdminOnlyCancel = "Termín, ktorý už začal, môže zrušiť iba administrátor.";
    public const string OtherEmployee = "Nemáte oprávnenie pracovať s termínmi iného pracovníka.";
    public const string InvalidAmount = "Suma musí byť od 0 do 10000 € a najviac na 2 desatinné miesta.";
    public const string CustomerNameEmpty = "Meno zákazníka nesmie byť prázdne.";
    public const string RetentionChannelRequired = "Kanál kontaktu je povinný a musí byť Phone, Email, Sms alebo InPerson.";
    public const string RetentionNoteInvalid = "Poznámka je povinná a môže mať najviac 2000 znakov.";
    public const string RetentionProbabilityInvalid = "Pravdepodobnosť odchodu musí byť číslo od 0 do 1.";
}

// ProblemDetails shortcuts shared by controllers.
public static class ProblemExtensions
{
    public static ObjectResult BadRequestProblem(this ControllerBase c, string detail) =>
        c.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request", detail: detail);

    public static ObjectResult ConflictProblem(this ControllerBase c, string detail) =>
        c.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: detail);

    public static ObjectResult ForbiddenProblem(this ControllerBase c, string detail) =>
        c.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: detail);
}
