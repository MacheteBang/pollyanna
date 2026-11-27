// Pollyanna's only script. Pages work without it; it adds the countdown and small conveniences.
"use strict";

// Countdown to the exchange date, from the viewer's own date (spec §4). The date is a plain
// calendar date with no time zone, so it is read as local midnight.
function countdowns() {
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    for (const el of document.querySelectorAll("[data-countdown]")) {
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
function previousEventPicker() {
    const select = document.querySelector("[data-previous-select]");
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
function addPeople() {
    const looksLikeEmail = s => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s);
    for (const form of document.querySelectorAll("[data-add-people]")) {
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
function gifts() {
    const still = window.matchMedia("(prefers-reduced-motion: reduce)");
    for (const gift of document.querySelectorAll("[data-gift]")) {
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

function conversations() {
    for (const list of document.querySelectorAll("[data-conversation]")) {
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
            const response = await fetch(form.action, { method: "POST", body: new FormData(form), redirect: "manual", credentials: "same-origin" });
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
}

// "12 / 1,000" under a message box as it fills.
function counters() {
    for (const el of document.querySelectorAll("[data-counter]")) {
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
function copyButtons() {
    if (!navigator.clipboard) return;
    for (const button of document.querySelectorAll("[data-copy]")) {
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
function saveOnChange() {
    for (const form of document.querySelectorAll("form[data-save-on-change]")) {
        for (const button of form.querySelectorAll("[data-save-button]")) button.hidden = true;
        form.addEventListener("change", () => form.requestSubmit());
    }
}

// A link to a fold on the same page, such as the blocker's "Add people" on the organizing page
// (spec §7): it opens the fold as well as moving to it. Without the script the page renders that
// fold open, so the link alone is enough.
function foldLinks() {
    for (const link of document.querySelectorAll("a[data-opens]")) {
        link.addEventListener("click", () => {
            const fold = document.getElementById(link.hash.slice(1));
            if (fold instanceof HTMLDetailsElement) fold.open = true;
        });
    }
}

document.addEventListener("DOMContentLoaded", () => {
    countdowns();
    copyButtons();
    previousEventPicker();
    addPeople();
    gifts();
    conversations();
    whatsNewTimes();
    whatsNewPanel();
    counters();
    saveOnChange();
    foldLinks();
});
