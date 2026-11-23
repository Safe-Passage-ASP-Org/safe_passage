export function printHtml(html) {
    const iframe = document.createElement("iframe");
    iframe.style.position = "fixed";
    iframe.style.right = "0";
    iframe.style.bottom = "0";
    iframe.style.width = "0";
    iframe.style.height = "0";
    iframe.style.border = "0";
    iframe.style.visibility = "hidden";
    document.body.appendChild(iframe);

    const frameWindow = iframe.contentWindow;
    const frameDoc = frameWindow && frameWindow.document;
    if (!frameDoc) {
        iframe.remove();
        window.print();
        return;
    }

    frameDoc.open();
    frameDoc.write(html);
    frameDoc.close();

    setTimeout(() => {
        try {
            frameWindow.focus();
            frameWindow.print();
        } finally {
            setTimeout(() => iframe.remove(), 1000);
        }
    }, 150);
}
