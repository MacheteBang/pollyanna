// PROTOTYPE (wayfinder #13). Throwaway: branch prototype/sign-in-chooser, never merged.
"use strict";
document.addEventListener("keydown", e => {
    if (e.target.closest("input, textarea, select, [contenteditable]")) return;
    const link = e.key === "ArrowLeft" ? document.querySelector("[data-proto-prev]")
        : e.key === "ArrowRight" ? document.querySelector("[data-proto-next]") : null;
    if (link) location.href = link.href;
});
