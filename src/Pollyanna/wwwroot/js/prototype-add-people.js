// PROTOTYPE (wayfinder #11). Throwaway: branch prototype/add-people, never merged.
"use strict";

const looksLikeEmail = s => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s);

// Arrow keys switch variants, unless typing.
document.addEventListener("keydown", e => {
    if (e.target.closest("input, textarea, select, [contenteditable]")) return;
    const link = e.key === "ArrowLeft" ? document.querySelector("[data-proto-prev]")
        : e.key === "ArrowRight" ? document.querySelector("[data-proto-next]") : null;
    if (link) location.href = link.href;
});

// A: combobox.
(() => {
    const form = document.querySelector('[data-proto="A"]');
    if (!form) return;
    const q = form.querySelector("#proto-a-q"), list = form.querySelector("#proto-a-list");
    const people = [...list.querySelectorAll("[data-id]")], invite = list.querySelector("[data-invite]");
    const personId = form.querySelector('input[name="personIds"]'), email = form.querySelector('input[name="email"]');
    const add = form.querySelector('button[type="submit"]'), picked = form.querySelector("[data-picked]");
    let active = -1;
    const visible = () => [...list.querySelectorAll('[role="option"]:not([hidden])')];
    const clear = () => { personId.disabled = email.disabled = add.disabled = true; picked.textContent = "Pick someone from the list, or type a full email address to invite it."; };
    const choose = opt => {
        clear();
        if (opt.dataset.id) { personId.value = opt.dataset.id; personId.disabled = false; q.value = opt.querySelector(".person-name").textContent; picked.textContent = "Adding " + q.value + "."; }
        else { email.value = q.value.trim(); email.disabled = false; picked.textContent = "Inviting " + email.value + "."; }
        add.disabled = false;
        list.hidden = true; q.setAttribute("aria-expanded", "false");
    };
    const render = () => {
        clear();
        const t = q.value.trim().toLowerCase();
        let any = false;
        for (const p of people) { p.hidden = !t || !p.dataset.search.includes(t); any ||= !p.hidden; }
        const exact = people.some(p => p.dataset.search.split(" ").includes(t));
        invite.hidden = !(looksLikeEmail(t) && !exact);
        invite.querySelector("[data-invite-address]").textContent = q.value.trim();
        list.hidden = !(any || !invite.hidden);
        q.setAttribute("aria-expanded", String(!list.hidden));
        active = -1; highlight();
    };
    const highlight = () => visible().forEach((o, i) => o.setAttribute("aria-selected", String(i === active)));
    q.addEventListener("input", render);
    q.addEventListener("keydown", e => {
        const opts = visible();
        if (e.key === "ArrowDown") { active = Math.min(active + 1, opts.length - 1); highlight(); e.preventDefault(); }
        else if (e.key === "ArrowUp") { active = Math.max(active - 1, 0); highlight(); e.preventDefault(); }
        else if (e.key === "Enter" && !list.hidden && opts.length) { choose(opts[Math.max(active, 0)]); e.preventDefault(); }
        else if (e.key === "Escape") { list.hidden = true; }
    });
    list.addEventListener("click", e => { const o = e.target.closest('[role="option"]'); if (o) choose(o); });
})();

// B: filtered ticklist.
(() => {
    const form = document.querySelector('[data-proto="B"]');
    if (!form) return;
    const q = form.querySelector("#proto-b-q"), rows = [...form.querySelectorAll("li[data-search]")];
    const invite = form.querySelector("[data-invite]"), box = invite.querySelector("input");
    q.addEventListener("input", () => {
        const t = q.value.trim().toLowerCase();
        for (const r of rows) r.hidden = t !== "" && !looksLikeEmail(t) && !r.dataset.search.includes(t);
        invite.hidden = !looksLikeEmail(t);
        invite.querySelector("[data-invite-address]").textContent = q.value.trim();
        box.value = q.value.trim();
    });
    q.addEventListener("keydown", e => { if (e.key === "Enter" && !looksLikeEmail(q.value.trim())) e.preventDefault(); });
})();
