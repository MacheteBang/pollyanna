// Pollyanna's only script. Pages work without it; it adds the countdown and small conveniences.
"use strict";

// The stylesheet tells pages with the script from those without (a conversation's scrolling box).
document.documentElement.classList.add("js");

// The page's parts are set up on load, and again on what a live update brings in (livePage), so
// each takes the root to look in. all(root, selector) includes the root itself when it matches.
const all = (root, selector) => [...(root.matches?.(selector) ? [root] : []), ...root.querySelectorAll(selector)];

// Puts a list's children in the order given (kept, new or moved), moving only those out of place,
// so a row that stays keeps its focus and nothing flashes: home's panels and the bell's rows.
function inOrder(list, children) {
    children.forEach((child, i) => {
        if (list.children[i] !== child) list.insertBefore(child, list.children[i] ?? null);
    });
}

// Countdown to the exchange date, from the viewer's own date (spec §4). The date is a plain
// calendar date with no time zone, so it is read as local midnight.
function countdowns(root = document) {
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    for (const el of all(root, "[data-countdown]")) {
        const [y, m, d] = el.dataset.countdown.split("-").map(Number);
        const days = Math.round((new Date(y, m - 1, d) - today) / 86400000);
        if (days < 0) continue;
        // "data-countdown-alone" stands on its own line ("Tablecloth"); otherwise it follows the date.
        const text = days === 0 ? "Today's the day" : days === 1 ? "1 day to go" : `${days} days to go`;
        el.textContent = "countdownAlone" in el.dataset ? text : ` · ${text}`;
        el.hidden = false;
    }
}

// New event: the don't-repeat choice shows only once an earlier event is picked (spec §7).
// Without the script it always shows, and is ignored when no earlier event is picked.
function previousEventPicker(root = document) {
    const select = all(root, "[data-previous-select]")[0];
    if (!select) return;
    const update = () => {
        for (const el of document.querySelectorAll("[data-needs-previous]"))
            el.hidden = select.value === "";
    };
    select.addEventListener("change", update);
    update();
}

// The add-people control (spec §7). Without the script it is a ticklist and an email field.
// With it, one box filters the list by name, and a full address in it shows a ticked
// "Add or invite <address>" row in place of the email field.
function addPeople(root = document) {
    const looksLikeEmail = s => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s);
    for (const form of all(root, "[data-add-people]")) {
        const filter = form.querySelector("[data-add-people-filter]");
        const field = form.querySelector("[data-add-people-email]");
        const row = form.querySelector("[data-add-people-address]");
        if (!filter || !field || !row) continue;
        const box = filter.querySelector("input");
        const address = row.querySelector("input");
        const rows = [...form.querySelectorAll("li[data-search]")];
        filter.hidden = false;
        field.hidden = true;
        field.querySelector("input").disabled = true;
        const update = () => {
            const typed = box.value.trim();
            const email = looksLikeEmail(typed);
            const term = typed.toLowerCase();
            for (const r of rows)
                r.hidden = term !== "" && !email && !r.dataset.search.includes(term);
            row.hidden = !email;
            address.disabled = !email;
            address.value = typed;
            row.querySelector("[data-add-people-typed]").textContent = typed;
        };
        box.addEventListener("input", update);
        // Enter adds an address, but never sends a half-typed filter.
        box.addEventListener("keydown", event => {
            if (event.key === "Enter" && !looksLikeEmail(box.value.trim())) event.preventDefault();
        });
        update();
    }
}

// The gift (spec §7, §12 The reveal): "Untie the bow", or a tap on the gift, unties it with the
// beats in tablecloth.css: the full sequence with the name written the first time, 0.4 times as
// long with the name already there on later visits, and nothing moving under reduce motion.
// Untying tells the server it was seen (by posting "Hide the name"'s own form in the
// background), which clears its markers; "Hide the name" then ties it back at once. Without the
// script, "Untie the bow" is a link to the page untied, and "Hide the name" posts that form.
function gifts(root = document) {
    const still = window.matchMedia("(prefers-reduced-motion: reduce)");
    for (const gift of all(root, "[data-gift]")) {
        const hide = gift.querySelector("form[data-hide]");
        const name = gift.querySelector(".revealed-name");
        const button = gift.querySelector(".gift-under [data-untie]");
        if (!hide || !name || !button) continue;
        let first = gift.dataset.first === "true";
        let told = false;
        const tell = () => {
            if (told) return;
            told = true;
            fetch(hide.action, { method: "POST", body: new FormData(hide), redirect: "manual", credentials: "same-origin" })
                .catch(() => { told = false; });
        };
        const untie = event => {
            event.preventDefault();
            if (gift.classList.contains("untied")) return;
            const motion = !still.matches;
            gift.classList.toggle("still", !motion);
            gift.classList.toggle("gift-first", motion && first);
            gift.classList.toggle("gift-later", !first);
            void gift.offsetWidth;
            gift.classList.add("untied");
            first = false;
            name.focus({ preventScroll: true });
            tell();
        };
        for (const el of gift.querySelectorAll("[data-untie]")) el.addEventListener("click", untie);
        hide.addEventListener("submit", event => {
            event.preventDefault();
            gift.classList.add("still");
            gift.classList.remove("untied", "gift-first");
            void gift.offsetWidth;
            button.focus();
        });
        // Rendered untied (just after asking something, say): it was seen.
        if (gift.classList.contains("untied")) tell();
    }
}

// "Questions for you" as a conversation (spec §7, §12): the page carries its day lines and
// times in UTC; this redoes them in the viewer's own time zone. A day line is "Today", the
// weekday within the week, otherwise the date; a time is "7:42 PM", or "about 7 PM" for a
// message stored to the hour before times were exact (data-said="hour").
// A day as a day line, from the viewer's own today (Format.DayLine on the server).
function dayLine(day) {
    const weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    const months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const ago = Math.round((today - day) / 86400000);
    return ago === 0 ? "Today"
        : ago > 0 && ago < 7 ? weekdays[day.getDay()]
        : `${months[day.getMonth()]} ${day.getDate()}${day.getFullYear() === today.getFullYear() ? "" : ", " + day.getFullYear()}`;
}

// "7:42 PM" in the viewer's own time zone.
function clockTime(at) {
    const h = at.getHours();
    return `${h % 12 || 12}:${String(at.getMinutes()).padStart(2, "0")} ${h < 12 ? "AM" : "PM"}`;
}

function conversations(root = document) {
    for (const list of all(root, "[data-conversation]")) {
        for (const line of list.querySelectorAll("[data-day]")) line.remove();
        let last = null;
        for (const message of list.querySelectorAll(".message")) {
            const time = message.querySelector("time[data-said]");
            const at = new Date(time?.getAttribute("datetime"));
            if (isNaN(at)) continue;
            const h = at.getHours();
            time.textContent = time.dataset.said === "hour"
                ? `about ${h === 0 ? "midnight" : h === 12 ? "noon" : `${h % 12} ${h < 12 ? "AM" : "PM"}`}`
                : clockTime(at);
            const day = new Date(at);
            day.setHours(0, 0, 0, 0);
            if (last !== null && day.getTime() === last) continue;
            last = day.getTime();
            const line = document.createElement("li");
            line.className = "day-line";
            line.dataset.day = "";
            line.textContent = dayLine(day);
            message.before(line);
        }
    }
}

// "What's new" times ("Today, 7:42 PM UTC" from the server) in the viewer's own time zone.
function whatsNewTimes() {
    for (const time of document.querySelectorAll("time[data-when]")) {
        const at = new Date(time.getAttribute("datetime"));
        if (isNaN(at)) continue;
        const day = new Date(at);
        day.setHours(0, 0, 0, 0);
        time.textContent = `${dayLine(day)}, ${clockTime(at)}`;
    }
}

// The bell's "What's new" panel (spec §7, §12, §16). The server renders the rows into every
// signed-in masthead, hidden; this swaps the bell's link for its button and opens the panel as a
// disclosure, not a dialog: focus stays on the bell, Tab goes into the panel (it follows the
// bell), and Escape or a tap outside closes it and puts focus back on the bell. A row's "×"
// posts the row's own form, the page's dismiss, in the background and removes the row; focus
// moves to the next row's "×", the previous one at the end, or the heading once it's empty.
function whatsNewPanel() {
    const button = document.querySelector("[data-bell]");
    const link = document.querySelector("[data-bell-link]");
    const panel = document.getElementById("whats-new-panel");
    if (!button || !link || !panel) return;
    const heading = panel.querySelector("#whats-new-panel-heading");
    const list = panel.querySelector(".bell-rows");
    const empty = panel.querySelector(".bell-empty");
    link.hidden = true;
    button.hidden = false;
    heading.tabIndex = -1; // focus lands here once the last row is dismissed

    const isOpen = () => !panel.hidden;
    const close = refocus => {
        panel.hidden = true;
        button.setAttribute("aria-expanded", "false");
        if (refocus) button.focus();
    };
    button.addEventListener("click", () => {
        if (isOpen()) return close(false);
        panel.hidden = false;
        button.setAttribute("aria-expanded", "true");
    });
    document.addEventListener("keydown", event => {
        if (event.key !== "Escape" || !isOpen()) return;
        close(true);
    });
    // A tap outside closes it; focus goes back to the bell unless the tap put it somewhere else
    // (a field, say).
    document.addEventListener("click", event => {
        if (!isOpen() || panel.contains(event.target) || button.contains(event.target)) return;
        const focus = document.activeElement;
        close(!focus || focus === document.body || panel.contains(focus));
    });

    // The bell's state and label follow the rows left; only the label counts them, in the
    // server's words (data-bell-labels: none, one, many with "{n}"). Its fill and red dot go together.
    let labels = [];
    try { labels = JSON.parse(button.dataset.bellLabels || "[]"); } catch { /* the label stays as it was */ }
    const update = () => {
        const count = list.children.length;
        const label = (labels[Math.min(count, 2)] ?? "").replace("{n}", String(count));
        for (const bell of [button, link]) {
            bell.classList.toggle("bell-new", count > 0);
            for (const dot of bell.querySelectorAll(".bell-dot")) dot.toggleAttribute("hidden", count === 0);
            if (label) bell.setAttribute("aria-label", label);
        }
        empty.hidden = count > 0;
    };

    list.addEventListener("submit", async event => {
        const form = event.target.closest("form[data-bell-dismiss]");
        if (!form) return;
        event.preventDefault();
        const row = form.closest("li");
        if ("going" in row.dataset) return;
        row.dataset.going = "";
        try {
            // Naming this tab leaves it out of the hint the dismissal sends to the person's other tabs.
            const headers = live.tab ? { "Pollyanna-Tab": live.tab } : {};
            const response = await fetch(form.action, { method: "POST", body: new FormData(form), headers, redirect: "manual", credentials: "same-origin" });
            if (response.type !== "opaqueredirect" && !response.ok) throw new Error(String(response.status));
        } catch {
            // Didn't go through: post it the way the page does, which lands on /whats-new.
            HTMLFormElement.prototype.submit.call(form);
            return;
        }
        const next = row.nextElementSibling ?? row.previousElementSibling;
        row.remove();
        update();
        (next?.querySelector(".bell-x") ?? heading).focus();
    });

    // A live refetch (liveConnection) brings the masthead as it is now. Rows already here stay
    // (so focus stays put) with their words brought up to date; new ones take their place in the
    // order without motion and are said politely; gone ones go.
    const say = document.querySelector("[data-bell-say]");
    const key = li => {
        const form = li.querySelector("form[data-bell-dismiss]");
        return form ? `${form.elements.event.value}|${form.elements.kind.value}` : "";
    };
    live.onRefetch.push(page => {
        const fresh = page?.querySelector("#whats-new-panel .bell-rows");
        if (!fresh) return;
        // A refetch a hint set off leaves out the rows tied to an assignment (data-live-pair):
        // the ones here stay, where they are.
        const pairsLeftOut = fresh.matches("[data-live-withheld]");
        const here = new Map([...list.children].map(li => [key(li), li]));
        const arrived = [];
        const rows = [...fresh.children].map(li => {
            const kept = here.get(key(li));
            if (!kept) {
                const row = document.importNode(li, true);
                arrived.push(row);
                return row;
            }
            here.delete(key(li));
            for (const part of [".bell-line", ".bell-meta"]) {
                const now = li.querySelector(part);
                const was = kept.querySelector(part);
                if (now && was && now.outerHTML !== was.outerHTML) was.replaceWith(document.importNode(now, true));
            }
            const x = kept.querySelector(".bell-x");
            const label = li.querySelector(".bell-x")?.getAttribute("aria-label");
            if (x && label) x.setAttribute("aria-label", label);
            // A question or reply row reads up to the message it now stands for, and no further.
            const form = kept.querySelector("form[data-bell-dismiss]");
            form?.querySelector('input[name="upto"]')?.remove();
            const upto = li.querySelector('input[name="upto"]');
            if (form && upto) form.prepend(document.importNode(upto, true));
            return kept;
        });
        if (pairsLeftOut)
            [...list.children].forEach((row, i) => {
                if (!row.matches("[data-live-pair]") || !here.has(key(row))) return;
                here.delete(key(row));
                rows.splice(Math.min(i, rows.length), 0, row);
            });
        let lostFocus = false;
        for (const gone of here.values()) {
            if ("going" in gone.dataset) continue; // its own dismissal is finishing it
            lostFocus ||= gone.contains(document.activeElement);
            gone.remove();
        }
        inOrder(list, rows);
        update();
        whatsNewTimes();
        if (lostFocus && isOpen()) heading.focus();
        if (say && arrived.length > 0)
            say.textContent = arrived.map(row => {
                const line = row.querySelector(".bell-line")?.textContent.trim() ?? "";
                const event = row.querySelector(".bell-meta")?.firstChild?.textContent.trim() ?? "";
                return event ? `${line}, ${event}` : line;
            }).join(". ");
    });
}

// The live connection (spec §16): every signed-in page holds one EventSource on /live. A hint
// makes the page do one live refetch of its own URL, marked by the Pollyanna-Live header
// (LiveRefetch.Header: the server treats it as a read-only view, not a visit), and hands the
// fetched page to whoever listens (live.onRefetch: the bell's panel and livePage), with what the
// hints said: listener(page, { areas, gone, moved }) (moved: the refetch was sent on elsewhere, and
// page is null). A hint names its refetch's header value: "hint" leaves the parts between a giver
// and a recipient out, since everyone it reached refetches with it; a catch-up (shown again,
// reconnected, or your own action in a pair) carries everything. Hints are ignored while a form
// post is in flight, since its response is a fresh page anyway. A hidden page closes its connection (and on pagehide), opens it
// again when shown and then refetches once; so does EventSource's own reconnect after a drop.
// Nothing is replayed. A refused connection (signed out, account deleted, session expired) stays
// closed until the next page load. Without the script, or without EventSource, nothing changes.
const live = { tab: "", posting: false, onRefetch: [] };

// A page that updates live (spec §16 "What each page does live"; home, the event page and organizing).
// The server marks it up (Pollyanna.Live.LiveShape and the page):
// - main[data-live-shape] names the page's shape, and data-live-line the quiet line for arriving
//   at it ("Names have been drawn."). A refetch with another shape is a state change.
// - [data-live][id] is a region that patches in place: replaced whole when the server's HTML for
//   it changed, unless something in it is being edited, and then tried again on the next refetch.
//   A region inside another is compared and patched on its own. A conversation ([data-thread])
//   is patched by adding its new messages inside its scrolling box.
// - [data-live-pair] marks a part between a giver and a recipient; [data-live-withheld] says a
//   refetch a hint set off left it out, so what this page shows of it stays (spec §16).
// - [data-live-list][id] holds regions (by id) that come and go, such as home's panels.
// Being edited: a form with focus or unsaved changes, or an open "Bigger changes" confirmation.
// The event page's gift is never a region, so its tied or untied state stays as the viewer left it
// while the parts under the name (their notes, "Done shopping") patch inside it.
// A giver's or recipient's own content frame (liveConnection) brings this page as they'd see it,
// patched the same way, and three things more: a patch brings the whole gift in again, tied, with
// the new name in it (never left on screen); a conversation's box with new messages scrolls to
// them if it was at the bottom, or shows a quiet "New message below", and says them politely; and the
// question box's Send is off while the conversation is closed, with any draft kept.
// Open folds stay open and focus stays where it was. A state change swaps the page body when
// nothing is being edited, and says the line politely; otherwise the line shows, with "Show",
// and the page stays as it is. A refetch that comes back not found means the event was canceled
// or the person is no longer in it. Nothing flashes or moves.
function livePage() {
    const main = document.querySelector("main[data-live-shape]");
    const say = main?.querySelector("[data-live-say]");
    if (!main || !main.dataset.liveShape || !say) return;
    // The words and hint areas the server gives for what a refetch can't show (LiveShape.Words).
    let words = {};
    try { words = JSON.parse(main.dataset.liveWords || "{}"); } catch { /* the lines just don't show */ }

    // Each region's HTML as the server sent it, to tell what changed: the page's own script changes
    // the regions (countdowns, folds, times), and every render has a new form token.
    // A region inside another is compared on its own, so the one around it changes only for
    // what's its own. That keeps a conversation's box in place while messages arrive in it, and a
    // part between a giver and a recipient (data-live-pair) that a refetch a hint set off leaves
    // out (data-live-withheld, spec §16) shows what it showed.
    const sent = new Map();
    const html = el => {
        const copy = el.cloneNode(true);
        for (const part of copy.querySelectorAll("[data-live][id], [data-live-pair]")) part.replaceWith(`[${part.id}]`);
        return copy.outerHTML.replace(/<input name="__RequestVerificationToken"[^>]*>/g, "");
    };
    const withheld = el => el.matches("[data-live-withheld]");
    const keep = root => {
        for (const el of all(root, "[data-live][id]")) sent.set(el.id, html(el));
    };
    keep(main);

    const dirty = form => [...form.elements].some(el => {
        if (el.disabled) return false;
        if (el instanceof HTMLTextAreaElement) return el.value !== el.defaultValue;
        if (el instanceof HTMLSelectElement) {
            const options = [...el.options];
            if (el.multiple) return options.some(o => o.selected !== o.defaultSelected);
            const first = options.findIndex(o => o.defaultSelected);
            return el.selectedIndex !== (first === -1 ? 0 : first);
        }
        if (!(el instanceof HTMLInputElement) || ["hidden", "submit", "button", "reset"].includes(el.type)) return false;
        if (el.type === "checkbox" || el.type === "radio") return el.checked !== el.defaultChecked;
        return el.value !== el.defaultValue;
    });
    const editing = root => {
        const focus = document.activeElement;
        return all(root, "form").some(form => form.contains(focus) || dirty(form))
            || all(root, ".change > details[open]").length > 0
            // A filter box (add people) lives in a form, but say so for any field outside one too.
            || (root.contains(focus) && focus.matches("input, textarea, select"));
    };

    const focusable = root => all(root, "a[href], button, input, select, textarea, summary");
    // Open folds stay open: the new copy's folds take this one's state, by id or else in order.
    const keepFolds = (was, now) => {
        const folds = root => all(root, "details");
        const byId = new Map(folds(was).filter(d => d.id).map(d => [d.id, d.open]));
        const plain = folds(was).filter(d => !d.id);
        let i = 0;
        for (const d of folds(now)) {
            if (d.id) { if (byId.has(d.id)) d.open = byId.get(d.id); }
            else if (i < plain.length) d.open = plain[i++].open;
        }
    };
    const swap = (was, fresh) => {
        const now = document.importNode(fresh, true);
        // A pair part the refetch left out keeps what this page shows of it, and a conversation
        // stays the box it is (scrolled where it was); the regions pass brings it up to date.
        for (const part of all(now, "[data-live-pair][data-live-withheld], [data-thread][id]")) {
            if (part === now) continue;
            const shown = was.querySelector(`#${CSS.escape(part.id)}`);
            if (shown) part.replaceWith(shown);
        }
        keepFolds(was, now);
        const focused = focusable(was).indexOf(document.activeElement);
        was.replaceWith(now);
        keep(now);
        pageParts(now);
        if (focused !== -1) focusable(now)[focused]?.focus({ preventScroll: true });
    };

    const lists = page => {
        for (const list of main.querySelectorAll("[data-live-list][id]")) {
            const fresh = page.getElementById(list.id);
            if (!fresh) continue;
            const here = new Map([...list.children].filter(c => c.id).map(c => [c.id, c]));
            const order = [...fresh.children].filter(c => c.id).map(c => {
                const kept = here.get(c.id);
                here.delete(c.id);
                if (kept) return kept;
                const added = document.importNode(c, true);
                keep(added);
                pageParts(added);
                return added;
            });
            for (const gone of here.values()) gone.remove();
            inOrder(list, order);
        }
    };
    const regions = page => {
        for (const region of main.querySelectorAll("[data-live][id]")) {
            if (!region.isConnected) continue; // inside one already replaced
            const fresh = page.getElementById(region.id);
            if (!fresh || withheld(fresh) || html(fresh) === sent.get(region.id) || editing(region)) continue;
            if (region.matches("[data-thread]") && addMessages(region, fresh)) {
                sent.set(region.id, html(fresh));
                continue;
            }
            swap(region, fresh);
        }
    };

    // The quiet line, in the polite live region: shown with a link, or only said.
    const line = (text, link, href) => {
        const p = document.createElement("p");
        if (!link) {
            p.className = "visually-hidden";
            p.textContent = text;
        } else {
            p.className = "notice live-line";
            const a = document.createElement("a");
            a.href = href;
            a.textContent = link;
            p.append(text, " ", a);
        }
        say.replaceChildren(p);
    };

    // The gift, again and tied, from a patch: the name in it changed (spec §16).
    const retie = page => {
        const gift = main.querySelector("#gift[data-gift]");
        const fresh = page.getElementById("gift");
        if (!gift || !fresh || withheld(fresh)) return;
        const now = document.importNode(fresh, true);
        now.classList.remove("untied", "still");
        gift.replaceWith(now);
        keep(now);
        pageParts(now);
    };

    // Threads (the questions page and the giver's question box, spec §16): each conversation is
    // its own scrolling box. New messages are added inside it, never by replacing the box, so the
    // page itself never moves. Read at the bottom, the box scrolls smoothly to them (at once under
    // reduce motion); scrolled up, a quiet "New message below" link points to them. Said politely.
    const still = window.matchMedia("(prefers-reduced-motion: reduce)");
    const box = thread => thread.querySelector(".thread-scroll");
    const atBottom = thread => {
        const b = box(thread);
        return !b || b.scrollHeight - b.scrollTop - b.clientHeight <= 24;
    };
    const toBottom = (thread, smooth) => {
        const b = box(thread);
        if (b) b.scrollTo({ top: b.scrollHeight, behavior: smooth && !still.matches ? "smooth" : "auto" });
    };
    // The same messages first, then more: those are added to the list in the box, and a message
    // whose "new" changed takes its new line; false (the whole region is replaced) otherwise.
    const addMessages = (region, fresh) => {
        const list = region.querySelector("ol.conversation");
        const freshList = fresh.querySelector("ol.conversation");
        if (!list || !freshList) return false;
        const have = [...list.querySelectorAll(".message")];
        const now = [...freshList.querySelectorAll(".message")];
        const same = (a, b) => a.querySelector("time")?.getAttribute("datetime") === b.querySelector("time")?.getAttribute("datetime")
            && a.querySelector(".bubble-body")?.textContent === b.querySelector(".bubble-body")?.textContent;
        if (now.length < have.length || have.some((m, i) => !same(m, now[i]))) return false;
        have.forEach((m, i) => {
            const isNew = now[i].querySelector(".message-new") !== null;
            if ((m.querySelector(".message-new") !== null) !== isNew)
                m.querySelector(".message-meta")?.replaceWith(document.importNode(now[i].querySelector(".message-meta"), true));
        });
        for (const m of now.slice(have.length)) list.append(document.importNode(m, true));
        conversations(region);
        return true;
    };
    // A thread inside a tied gift isn't on screen: nothing scrolls to it or reads it out.
    const shown = thread => !thread.closest("[data-gift]:not(.untied)");
    const below = thread => {
        if (thread.querySelector(".new-below")) return;
        const p = document.createElement("p");
        p.className = "new-below";
        const a = document.createElement("a");
        a.href = `#${thread.id}`;
        a.textContent = "New message below";
        const gone = () => {
            if (p.isConnected && !atBottom(thread)) return;
            p.remove();
            box(thread)?.removeEventListener("scroll", gone);
        };
        a.addEventListener("click", event => {
            event.preventDefault();
            toBottom(thread, true);
            p.remove();
        });
        p.append(a);
        thread.append(p);
        box(thread)?.addEventListener("scroll", gone, { passive: true });
    };
    const watchThreads = () => {
        const before = new Map(all(main, "[data-thread][id]").map(t => [t.id, { count: t.querySelectorAll(".message").length, bottom: atBottom(t) }]));
        return () => {
            const said = [];
            for (const [id, was] of before) {
                const thread = document.getElementById(id);
                if (!thread || !shown(thread)) continue;
                const messages = [...thread.querySelectorAll(".message")];
                if (messages.length <= was.count) continue;
                if (was.bottom) toBottom(thread, true);
                else below(thread);
                for (const m of messages.slice(was.count))
                    said.push(`New message: ${m.querySelector(".bubble-body")?.textContent.trim() ?? ""}`);
            }
            if (said.length > 0) line(said.join(" "));
        };
    };
    // Closed: Send is off, and a draft stays in the box; reopened, it's on again.
    const sendState = () => {
        const closed = main.querySelector("#ask-closed [data-closed]") !== null;
        for (const button of main.querySelectorAll('#ask form button[type="submit"]')) button.disabled = closed;
    };

    let stale = false; // the line is up: this page is no longer what the server would send
    const body = (page, fresh) => {
        const was = [...main.querySelectorAll("details[id]")].map(d => [d.id, d.open]);
        for (const notice of fresh.querySelectorAll(".notice")) notice.remove(); // left for the next visit
        // A gift arrives tied, whatever the refetch said (a waiting "untied" is for the next
        // visit, like a notice): nothing shows the name on its own.
        for (const gift of fresh.querySelectorAll("[data-gift]")) gift.classList.remove("untied", "still");
        const nodes = [...fresh.childNodes].map(n => document.importNode(n, true));
        main.replaceChildren(...nodes);
        const slot = main.querySelector("[data-live-say]");
        if (slot) slot.replaceWith(say);
        else main.prepend(say);
        say.replaceChildren();
        main.dataset.liveShape = fresh.dataset.liveShape;
        main.dataset.liveLine = fresh.dataset.liveLine ?? "";
        for (const [id, open] of was) {
            const d = document.getElementById(id);
            if (d instanceof HTMLDetailsElement) d.open = open;
        }
        if (page.title) document.title = page.title;
        sent.clear();
        keep(main);
        pageParts(main);
    };

    live.onRefetch.push((page, hint) => {
        // Moved: the questions page sends a refetch on to the event page once names are revealed
        // (or the event archived). That's a state change; the line shows with "Show", which goes there.
        if (hint.moved) {
            if (!main.dataset.liveMoved) return;
            const text = (hint.areas.has(words.archivedArea) ? words.archived : words.revealed) ?? "";
            if (text) line(text, "Show", main.dataset.liveMoved);
            stale = true;
            return;
        }
        if (hint.gone) {
            const text = (hint.areas.has(words.canceledArea) ? words.canceled : words.out) ?? "";
            if (!editing(main)) main.replaceChildren(say);
            line(text, words.back ?? "", "/");
            stale = true;
            return;
        }
        // Content for another page (liveConnection): only the masthead's rows were for this one.
        if (hint.elsewhere) return;
        const fresh = page.querySelector("main[data-live-shape]");
        if (!fresh?.dataset.liveShape) return;
        if (fresh.dataset.liveShape === main.dataset.liveShape) {
            if (!stale) {
                if (hint.areas.has(words.patchedArea)) retie(page);
                const threads = watchThreads();
                lists(page);
                regions(page);
                threads();
                sendState();
            }
            return;
        }
        const text = fresh.dataset.liveLine ?? "";
        // A refetch a hint set off left the pair parts out (the gift's name, say), so it can't
        // stand as the page: the line shows with "Show", whose load brings them.
        if (!stale && !editing(main) && fresh.querySelector("[data-live-withheld]") === null) {
            body(page, fresh);
            if (text) line(text);
            return;
        }
        if (text) line(text, "Show", location.href);
        stale = true;
    });
}

function liveConnection() {
    if (!document.querySelector("[data-bell]") || !("EventSource" in window)) return;
    let source = null;
    let opened = false;
    let refused = false;
    let fetching = false;
    let again = false;

    // The areas (happening kinds) of the hints since the last refetch: a page can tell a
    // cancellation from being taken out. A catch-up refetch has none.
    let areas = new Set();
    // What the next refetch sends in its Pollyanna-Live header (LiveRefetch): the value a hint
    // names ("hint" leaves the parts between a giver and a recipient out, since everyone the hint
    // reached refetches with it), or a catch-up, which carries everything. A catch-up wins: it's
    // after being away, or after your own action in a pair, which only your own tabs hear of.
    const catchUp = "catch-up"; // any value but the hint's
    let as = null;
    const hinted = event => {
        let named = null;
        try {
            const data = JSON.parse(event.data);
            if (typeof data.area === "string") areas.add(data.area);
            if (typeof data.refetch === "string") named = data.refetch;
        } catch {
            // A hint without data still refetches.
        }
        refetch(named ?? catchUp);
    };

    // A giver-recipient happening (spec §16) comes as content: the pages it touches, as this person
    // sees them, by path, so nothing is fetched after it (a fetch would pair them in the proxy log).
    // The page that matches this one is handed on as a refetch would be; any other page takes only
    // the masthead's rows from it. Its form tokens come empty (the server can't make one for this
    // browser's cookie), so this page's own goes in. Ignored while a post is in flight, like hints.
    const content = event => {
        if (live.posting) return;
        let frame;
        try {
            frame = JSON.parse(event.data);
        } catch {
            return;
        }
        const pages = frame?.pages ?? {};
        // This page's own path as the server files its content (data-live-path; /untie is the event page).
        const path = document.querySelector("main")?.dataset.livePath || location.pathname;
        const html = pages[path] ?? pages["/"] ?? Object.values(pages)[0];
        if (typeof html !== "string") return;
        const page = new DOMParser().parseFromString(html, "text/html");
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";
        for (const input of page.querySelectorAll('input[name="__RequestVerificationToken"]')) input.setAttribute("value", token);
        const hint = { areas: new Set(typeof frame.area === "string" ? [frame.area] : []), gone: false, elsewhere: !(path in pages) };
        for (const listener of live.onRefetch) listener(page, hint);
    };

    const refetch = async value => {
        as = as === catchUp || value === catchUp ? catchUp : value;
        if (live.posting || document.hidden) return;
        if (fetching) { again = true; return; }
        fetching = true;
        const hint = { areas, gone: false, moved: false };
        const header = as;
        areas = new Set();
        as = null;
        try {
            const response = await fetch(location.href, {
                headers: { "Pollyanna-Live": header }, redirect: "manual", credentials: "same-origin", cache: "no-store",
            });
            // Sent on elsewhere: the questions page, once names are revealed (a state change).
            hint.moved = response.type === "opaqueredirect";
            if (hint.moved && !live.posting) {
                for (const listener of live.onRefetch) listener(null, hint);
                return;
            }
            // Not found: the event was canceled, or this person is no longer in it. That page
            // still has the masthead, so the bell's panel is brought up to date all the same.
            hint.gone = response.status === 404;
            if ((response.ok || hint.gone) && !live.posting) {
                const page = new DOMParser().parseFromString(await response.text(), "text/html");
                for (const listener of live.onRefetch) listener(page, hint);
            }
        } catch {
            // The next hint or reconnect tries again.
        } finally {
            fetching = false;
            if (again) { again = false; refetch(as ?? catchUp); }
        }
    };

    const open = () => {
        if (source || refused || document.hidden) return;
        source = new EventSource("/live");
        source.addEventListener("hello", event => { live.tab = event.data; });
        source.addEventListener("hint", hinted);
        source.addEventListener("content", content);
        // The first open is the page load itself; any later one (shown again, reconnected) catches up.
        source.addEventListener("open", () => {
            if (opened) refetch(catchUp);
            opened = true;
        });
        source.addEventListener("error", () => {
            // CLOSED means the server refused it (a status, not a stream); otherwise it's reconnecting.
            if (source?.readyState !== EventSource.CLOSED) return;
            source = null;
            live.tab = "";
            refused = true;
        });
    };
    const close = () => {
        source?.close();
        source = null;
        live.tab = "";
    };

    document.addEventListener("visibilitychange", () => (document.hidden ? close() : open()));
    window.addEventListener("pagehide", close);
    window.addEventListener("pageshow", () => {
        live.posting = false; // back from the cache after a post navigated away
        open();
    });
    // A form that really posts (not one a script took over) is in flight until its page loads.
    document.addEventListener("submit", event => {
        if (!event.defaultPrevented) live.posting = true;
    });
    open();
}

// A conversation's box starts at its newest message: on load, and when one comes in whole. Once
// set, live updates scroll it (livePage), so a box brought along by a swap stays where it was.
function threadBoxes(root = document) {
    for (const box of all(root, ".thread-scroll")) {
        if ("placed" in box.dataset) continue;
        box.dataset.placed = "";
        box.scrollTop = box.scrollHeight;
    }
}

// The message boxes (the giver's question box and the recipient's reply box, spec §7): Enter
// sends at once and Shift+Enter starts a new line, the same on both sides. Not while an input
// method is composing a character. An empty box is stopped by "required", as the button is.
// Without the script, the button sends.
function composers(root = document) {
    for (const form of all(root, "form[data-composer]")) {
        const box = form.querySelector("textarea");
        if (!box || "enterSends" in box.dataset) continue;
        box.dataset.enterSends = "";
        box.addEventListener("keydown", event => {
            if (event.key !== "Enter" || event.shiftKey || event.isComposing) return;
            event.preventDefault();
            form.requestSubmit();
        });
    }
}

// "12 / 1,000" under a message box as it fills.
function counters(root = document) {
    for (const el of all(root, "[data-counter]")) {
        const box = document.getElementById(el.dataset.counter);
        if (!box) continue;
        const update = () => {
            el.textContent = `${box.value.length.toLocaleString("en-US")} / ${box.maxLength.toLocaleString("en-US")}`;
        };
        box.addEventListener("input", update);
        update();
    }
}

// Empty home: a "Copy" button beside the viewer's address. Without the script (or a
// clipboard) the button stays hidden and the address can be selected by hand.
function copyButtons(root = document) {
    if (!navigator.clipboard) return;
    for (const button of all(root, "[data-copy]")) {
        button.hidden = false;
        button.addEventListener("click", async () => {
            try {
                await navigator.clipboard.writeText(button.dataset.copy);
                button.textContent = "Copied";
            } catch {
                // Clipboard refused: the address is still there to select.
            }
        });
    }
}

// A form that saves as soon as its control changes, such as the email switch on /account
// (spec §15). Without the script its Save button shows and does the same.
function saveOnChange(root = document) {
    for (const form of all(root, "form[data-save-on-change]")) {
        for (const button of form.querySelectorAll("[data-save-button]")) button.hidden = true;
        form.addEventListener("change", () => form.requestSubmit());
    }
}

// A link to a fold on the same page, such as the blocker's "Add people" on the organizing page
// (spec §7): it opens the fold as well as moving to it. Without the script the page renders that
// fold open, so the link alone is enough.
function foldLinks(root = document) {
    for (const link of all(root, "a[data-opens]")) {
        link.addEventListener("click", () => {
            const fold = document.getElementById(link.hash.slice(1));
            if (fold instanceof HTMLDetailsElement) fold.open = true;
        });
    }
}

// Everything inside the page body that a live update can bring in.
function pageParts(root = document) {
    countdowns(root);
    copyButtons(root);
    previousEventPicker(root);
    addPeople(root);
    gifts(root);
    conversations(root);
    threadBoxes(root);
    composers(root);
    counters(root);
    saveOnChange(root);
    foldLinks(root);
    whatsNewTimes();
}

document.addEventListener("DOMContentLoaded", () => {
    livePage(); // first, to keep each region as the server sent it, before the parts change it
    pageParts();
    whatsNewPanel();
    liveConnection();
});
