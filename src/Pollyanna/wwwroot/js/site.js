// Pollyanna's only script. Pages work without it; it adds the countdown and small conveniences.
"use strict";

// The page's parts are set up on load, and again on what a live update brings in (livePage), so
// each takes the root to look in. all(root, selector) includes the root itself when it matches.
const all = (root, selector) => [...(root.matches?.(selector) ? [root] : []), ...root.querySelectorAll(selector)];

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

    // The bell's state and label follow the rows left; only the label counts them.
    const update = () => {
        const count = list.children.length;
        const label = count === 0 ? "What's new, nothing new"
            : count === 1 ? "What's new, 1 notification"
            : `What's new, ${count} notifications`;
        for (const bell of [button, link]) {
            bell.classList.toggle("bell-new", count > 0);
            bell.setAttribute("aria-label", label);
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
        const fresh = page.querySelector("#whats-new-panel .bell-rows");
        if (!fresh) return;
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
            return kept;
        });
        let lostFocus = false;
        for (const gone of here.values()) {
            if ("going" in gone.dataset) continue; // its own dismissal is finishing it
            lostFocus ||= gone.contains(document.activeElement);
            gone.remove();
        }
        rows.forEach((row, i) => {
            if (list.children[i] !== row) list.insertBefore(row, list.children[i] ?? null);
        });
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
// hints said: listener(page, { areas, gone }). Hints are ignored while a form post is in flight, since its
// response is a fresh page anyway. A hidden page closes its connection (and on pagehide), opens it
// again when shown and then refetches once; so does EventSource's own reconnect after a drop.
// Nothing is replayed. A refused connection (signed out, account deleted, session expired) stays
// closed until the next page load. Without the script, or without EventSource, nothing changes.
const live = { tab: "", posting: false, onRefetch: [] };

// A page that updates live (spec §16 "What each page does live"; home and organizing so far).
// The server marks it up (Pollyanna.Live.LiveShape and the page):
// - main[data-live-shape] names the page's shape, and data-live-line the quiet line for arriving
//   at it ("Names have been drawn."). A refetch with another shape is a state change.
// - [data-live][id] is a region that patches in place: replaced whole when the server's HTML for
//   it changed, unless something in it is being edited, and then tried again on the next refetch.
// - [data-live-list][id] holds regions (by id) that come and go, such as home's panels.
// Being edited: a form with focus or unsaved changes, or an open "Bigger changes" confirmation.
// Open folds stay open and focus stays where it was. A state change swaps the page body when
// nothing is being edited, and says the line politely; otherwise the line shows, with "Show",
// and the page stays as it is. A refetch that comes back not found means the event was canceled
// or the person is no longer in it. Nothing flashes or moves.
function livePage() {
    const main = document.querySelector("main[data-live-shape]");
    const say = main?.querySelector("[data-live-say]");
    if (!main || !main.dataset.liveShape || !say) return;

    // Each region's HTML as the server sent it, to tell what changed: the page's own script changes
    // the regions (countdowns, folds, times), and every render has a new form token.
    const sent = new Map();
    const html = el => el.outerHTML.replace(/<input name="__RequestVerificationToken"[^>]*>/g, "");
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
            order.forEach((row, i) => {
                if (list.children[i] !== row) list.insertBefore(row, list.children[i] ?? null);
            });
        }
    };
    const regions = page => {
        for (const region of main.querySelectorAll("[data-live][id]")) {
            if (!region.isConnected) continue; // inside one already replaced
            const fresh = page.getElementById(region.id);
            if (!fresh || html(fresh) === sent.get(region.id) || editing(region)) continue;
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

    let stale = false; // the line is up: this page is no longer what the server would send
    const body = (page, fresh) => {
        const was = [...main.querySelectorAll("details[id]")].map(d => [d.id, d.open]);
        for (const notice of fresh.querySelectorAll(".notice")) notice.remove(); // left for the next visit
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
        if (hint.gone) {
            const text = hint.areas.has("EventCanceled") ? "This event was canceled" : "You're no longer in this event";
            if (!editing(main)) main.replaceChildren(say);
            line(text, "Back to your events", "/");
            stale = true;
            return;
        }
        const fresh = page.querySelector("main[data-live-shape]");
        if (!fresh?.dataset.liveShape) return;
        if (fresh.dataset.liveShape === main.dataset.liveShape) {
            if (!stale) { lists(page); regions(page); }
            return;
        }
        const text = fresh.dataset.liveLine ?? "";
        if (!stale && !editing(main)) {
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
    const hinted = event => {
        try {
            const area = JSON.parse(event.data).area;
            if (typeof area === "string") areas.add(area);
        } catch {
            // A hint without data still refetches.
        }
        refetch();
    };

    const refetch = async () => {
        if (live.posting || document.hidden) return;
        if (fetching) { again = true; return; }
        fetching = true;
        const hint = { areas, gone: false };
        areas = new Set();
        try {
            const response = await fetch(location.href, {
                headers: { "Pollyanna-Live": "1" }, redirect: "manual", credentials: "same-origin", cache: "no-store",
            });
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
            if (again) { again = false; refetch(); }
        }
    };

    const open = () => {
        if (source || refused || document.hidden) return;
        source = new EventSource("/live");
        source.addEventListener("hello", event => { live.tab = event.data; });
        source.addEventListener("hint", hinted);
        // The first open is the page load itself; any later one (shown again, reconnected) catches up.
        source.addEventListener("open", () => {
            if (opened) refetch();
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
