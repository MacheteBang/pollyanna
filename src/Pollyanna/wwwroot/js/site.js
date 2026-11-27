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
                r.hidden = r.dataset.prefilled === "true" || (term !== "" && !email && !r.dataset.search.includes(term));
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

// The reveal card (spec §7, §12): a <details> tied with ribbon. Pulling the ribbon unties the
// bow, then the slip unfolds. Opening it tells the server the slip was seen, which clears its
// markers; "Tie it back up" then just closes it. Without the script, the card opens at once,
// and "Tie it back up" posts the same thing as a form.
function revealCards() {
    const still = window.matchMedia("(prefers-reduced-motion: reduce)");
    for (const card of document.querySelectorAll("[data-reveal]")) {
        const fold = card.querySelector("[data-fold]");
        if (!fold) continue;
        card.querySelector("summary").addEventListener("click", event => {
            if (card.open || still.matches) return;
            event.preventDefault();
            if (card.classList.contains("untying")) return;
            card.classList.add("untying");
            setTimeout(() => { card.open = true; }, 600);
        });
        let told = false;
        card.addEventListener("toggle", () => {
            if (!card.open) card.classList.remove("untying");
            if (!card.open || told) return;
            told = true;
            fetch(fold.action, { method: "POST", body: new FormData(fold), redirect: "manual", credentials: "same-origin" })
                .catch(() => { told = false; });
        });
        fold.addEventListener("submit", event => {
            event.preventDefault();
            card.open = false;
            card.querySelector("summary").focus();
        });
    }
}

// Question box times (spec §6, §12): "MON, ABOUT 7 PM" in the viewer's time zone, "TODAY" for
// today, the date for anything older than a week. Times are stored on the hour; without the
// script, the page shows the date alone.
function messageTimes() {
    const days = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
    const months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    for (const el of document.querySelectorAll("time[data-hour]")) {
        const time = new Date(el.getAttribute("datetime"));
        if (isNaN(time)) continue;
        const day = new Date(time);
        day.setHours(0, 0, 0, 0);
        const ago = Math.round((today - day) / 86400000);
        const date = ago === 0 ? "TODAY"
            : ago > 0 && ago < 7 ? days[day.getDay()]
            : `${months[day.getMonth()]} ${day.getDate()}${day.getFullYear() === today.getFullYear() ? "" : ", " + day.getFullYear()}`;
        const h = time.getHours();
        const hour = h === 0 ? "MIDNIGHT" : h === 12 ? "NOON" : `${h % 12} ${h < 12 ? "AM" : "PM"}`;
        el.textContent = `${date}, ABOUT ${hour}`;
    }
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

// A form that saves as soon as its control changes, such as the email switch on /account
// (spec §15). Without the script its Save button shows and does the same.
function saveOnChange() {
    for (const form of document.querySelectorAll("form[data-save-on-change]")) {
        for (const button of form.querySelectorAll("[data-save-button]")) button.hidden = true;
        form.addEventListener("change", () => form.requestSubmit());
    }
}

document.addEventListener("DOMContentLoaded", () => {
    countdowns();
    previousEventPicker();
    addPeople();
    revealCards();
    messageTimes();
    counters();
    saveOnChange();
});
