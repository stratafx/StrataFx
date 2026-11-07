// Column resizing for StrataFx list views. Dragging a column's right edge resizes it in the browser only; the
// widths go to .NET once, when the drag ends, so a resize is one save instead of one per pixel.
const minWidth = 48;
const maxWidth = 1200;

export function attach(root, dotnet) {
    let drag = null;

    const columnOf = (table, name) => [...table.querySelectorAll("col[data-col]")].find(c => c.dataset.col === name);

    // Switches the table to fixed layout at the widths it has now, so dragging one column moves only that column.
    const freeze = table => {
        if (root.classList.contains("strata-list-sized")) return;
        for (const th of table.querySelectorAll("thead th[data-col]")) {
            const col = columnOf(table, th.dataset.col);
            if (col) col.style.width = Math.round(th.getBoundingClientRect().width) + "px";
        }
        root.classList.add("strata-list-sized");
    };

    const widths = table => {
        const result = {};
        for (const th of table.querySelectorAll("thead th[data-col]"))
            result[th.dataset.col] = Math.round(th.getBoundingClientRect().width);
        return result;
    };

    const onPointerDown = e => {
        const handle = e.target.closest(".strata-col-resizer");
        if (!handle || !root.contains(handle) || e.button !== 0) return;
        const table = handle.closest("table");
        const th = handle.closest("th");
        const col = columnOf(table, handle.dataset.col);
        if (!table || !th || !col) return;

        e.preventDefault();
        e.stopPropagation();
        freeze(table);
        drag = { handle, table, col, pointerId: e.pointerId, startX: e.clientX, startWidth: th.getBoundingClientRect().width };
        handle.setPointerCapture(e.pointerId);
        root.classList.add("strata-resizing");
    };

    const onPointerMove = e => {
        if (!drag || e.pointerId !== drag.pointerId) return;
        const width = Math.min(maxWidth, Math.max(minWidth, Math.round(drag.startWidth + e.clientX - drag.startX)));
        drag.col.style.width = width + "px";
    };

    const onPointerUp = e => {
        if (!drag || e.pointerId !== drag.pointerId) return;
        const { handle, table } = drag;
        drag = null;
        root.classList.remove("strata-resizing");
        if (handle.hasPointerCapture(e.pointerId)) handle.releasePointerCapture(e.pointerId);
        dotnet.invokeMethodAsync("OnColumnsResized", widths(table)).catch(() => { /* circuit gone */ });
    };

    // A click on the edge must not sort the column, and a double-click gives it its automatic width back.
    const onClick = e => {
        if (e.target.closest(".strata-col-resizer")) {
            e.preventDefault();
            e.stopPropagation();
        }
    };
    const onDoubleClick = e => {
        const handle = e.target.closest(".strata-col-resizer");
        if (!handle || !root.contains(handle)) return;
        e.preventDefault();
        e.stopPropagation();
        dotnet.invokeMethodAsync("OnColumnAutoWidth", handle.dataset.col).catch(() => { /* circuit gone */ });
    };

    root.addEventListener("pointerdown", onPointerDown);
    root.addEventListener("pointermove", onPointerMove);
    root.addEventListener("pointerup", onPointerUp);
    root.addEventListener("pointercancel", onPointerUp);
    root.addEventListener("click", onClick, true);
    root.addEventListener("dblclick", onDoubleClick, true);

    return {
        dispose() {
            root.removeEventListener("pointerdown", onPointerDown);
            root.removeEventListener("pointermove", onPointerMove);
            root.removeEventListener("pointerup", onPointerUp);
            root.removeEventListener("pointercancel", onPointerUp);
            root.removeEventListener("click", onClick, true);
            root.removeEventListener("dblclick", onDoubleClick, true);
        },
    };
}
