using Pollyanna.Auth;
using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.People;

public static class PictureEndpoint
{
    /// <summary>
    /// <c>GET /people/{id}/picture</c>: the stored picture, for the person themselves, anyone
    /// who shares an event with them, and organizers adding people (spec §3), who pick from
    /// everyone who has signed in. Everyone else gets 404, so the endpoint doesn't reveal who has
    /// a picture.
    /// </summary>
    public static void MapPictures(this IEndpointRouteBuilder app) =>
        app.MapGet("/people/{id:guid}/picture", async (Guid id, HttpContext http, PollyannaDb db) =>
        {
            var me = http.User.PersonId();
            if (id != me && !await SharesEventAsync(db, me, id) && !await IsAddingPeopleAsync(db, me))
                return Results.NotFound();

            var picture = await db.PersonPictures.AsNoTracking().SingleOrDefaultAsync(p => p.PersonId == id);
            if (picture is null)
                return Results.NotFound();

            http.Response.Headers.CacheControl = "private, max-age=300";
            return Results.Bytes(picture.Data, picture.ContentType);
        });

    /// <summary>An organizer of an event that people can still be added to.</summary>
    static Task<bool> IsAddingPeopleAsync(PollyannaDb db, Guid me) =>
        db.Organizers.AnyAsync(o => o.PersonId == me
            && db.Events.Any(e => e.Id == o.EventId && EventLifecycle.BeforeDraw.Contains(e.Status)));

    static Task<bool> SharesEventAsync(PollyannaDb db, Guid a, Guid b)
    {
        var eventsOfA = db.Participants.Where(p => p.PersonId == a).Select(p => p.EventId)
            .Union(db.Organizers.Where(o => o.PersonId == a).Select(o => o.EventId));
        return db.Participants.Where(p => p.PersonId == b).Select(p => p.EventId)
            .Union(db.Organizers.Where(o => o.PersonId == b).Select(o => o.EventId))
            .AnyAsync(e => eventsOfA.Contains(e));
    }
}
