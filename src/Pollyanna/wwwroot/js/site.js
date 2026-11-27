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

// "Questions for you" as a conversation (spec §7, §12): the page carries its day lines and
// times in UTC; this redoes them in the viewer's own time zone. A day line is "Today", the
// weekday within the week, otherwise the date; a time is "about 7 PM".
function conversations() {
    const weekdays = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    const months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const dayLine = day => {
        const ago = Math.round((today - day) / 86400000);
        return ago === 0 ? "Today"
            : ago > 0 && ago < 7 ? weekdays[day.getDay()]
            : `${months[day.getMonth()]} ${day.getDate()}${day.getFullYear() === today.getFullYear() ? "" : ", " + day.getFullYear()}`;
    };
    for (const list of document.querySelectorAll("[data-conversation]")) {
        for (const line of list.querySelectorAll("[data-day]")) line.remove();
        let last = null;
        for (const message of list.querySelectorAll(".message")) {
            const time = message.querySelector("time[data-said]");
            const at = new Date(time?.getAttribute("datetime"));
            if (isNaN(at)) continue;
            const h = at.getHours();
            time.textContent = `about ${h === 0 ? "midnight" : h === 12 ? "noon" : `${h % 12} ${h < 12 ? "AM" : "PM"}`}`;
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
    gifts();
    messageTimes();
    conversations();
    counters();
    saveOnChange();
});
