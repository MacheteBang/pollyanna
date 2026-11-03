using Pollyanna.Data;
using Pollyanna.Events;

namespace Pollyanna.Pages.Shared;

/// <summary>
/// A question thread drawn as passed notes (spec §12): the viewer's own on slip paper, the other
/// side's on bright paper with a red top edge. <paramref name="Them"/> labels the other side,
/// e.g. "Carol replied" or "Giver"; CSS shows it in capitals.
/// </summary>
public sealed record NotesView(QuestionThread Thread, MessageSender Viewer, string Them)
{
    public string Mine => Viewer == MessageSender.Giver ? "You asked" : "You replied";
}
