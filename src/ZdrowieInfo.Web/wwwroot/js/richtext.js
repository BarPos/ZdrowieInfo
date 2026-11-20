// Edytor treści wpisu własnego: contenteditable poza drzewem renderowania Blazora (element jest w nim pusty),
// pasek narzędzi obsługiwany tutaj (mousedown bez utraty zaznaczenia). Przed wysłaniem do serwera HTML jest odchudzany
// (komentarze, style, klasy, atrybuty Worda/Outlooka) – pełne oczyszczenie robi serwer z listy dozwolonych znaczników.

const DROP = new Set(["script", "style", "meta", "link", "title", "head", "noscript", "template", "svg", "math", "iframe",
    "object", "embed", "img", "picture", "video", "audio", "canvas", "form", "input", "button", "select", "textarea", "xml"]);

const TABLE = "<table><tr><th>Nagłówek</th><th>Nagłówek</th></tr><tr><td>&nbsp;</td><td>&nbsp;</td></tr></table><p><br></p>";

export function init(editor, toolbar, html) {
    editor.innerHTML = html || "";
    toolbar.querySelectorAll("[data-cmd]").forEach(button => {
        button.addEventListener("mousedown", event => {
            event.preventDefault(); // zaznaczenie w edytorze zostaje
            const cmd = button.dataset.cmd;
            if (cmd === "table") document.execCommand("insertHTML", false, TABLE);
            else if (cmd === "h3") document.execCommand("formatBlock", false, "h3");
            else if (cmd === "p") document.execCommand("formatBlock", false, "p");
            else document.execCommand(cmd, false);
            editor.focus();
        });
    });
}

export function setHtml(editor, html) {
    editor.innerHTML = html || "";
}

export function getHtml(editor) {
    const copy = editor.cloneNode(true);
    clean(copy);
    return copy.innerHTML.replace(/\s+/g, " ").trim();
}

function clean(node) {
    for (const child of [...node.childNodes]) {
        if (child.nodeType === Node.COMMENT_NODE) { child.remove(); continue; }
        if (child.nodeType !== Node.ELEMENT_NODE) continue;
        const tag = child.localName;
        if (DROP.has(tag) || tag.includes(":") && tag !== "o:p") { child.remove(); continue; }
        for (const attribute of [...child.attributes]) {
            const keep = (tag === "a" && attribute.name === "href")
                || ((tag === "td" || tag === "th") && (attribute.name === "colspan" || attribute.name === "rowspan"));
            if (!keep) child.removeAttribute(attribute.name);
        }
        clean(child);
    }
}
