#!/usr/bin/env node
/*
 * Refreshes the PMDG SDK header embedded in pmdg-fields.html.
 *
 * Usage:
 *   node tools/update-pmdg-fields.js <path-or-url-to-PMDG_NG3_SDK.h>
 *
 * The installed header lives next to the aircraft, typically:
 *   ...\Community\pmdg-aircraft-738\Documentation\SDK\PMDG_NG3_SDK.h
 *
 * Run this after a PMDG update; the page itself can also load a newer header
 * at runtime through its file picker, so this is only needed to update the
 * copy that is checked in.
 */
const fs = require("fs");
const path = require("path");

const BEGIN = "<!-- BEGIN SDK HEADER -->";
const END = "<!-- END SDK HEADER -->";

async function readSource(src) {
    if (/^https?:\/\//i.test(src)) {
        const resp = await fetch(src);
        if (!resp.ok) throw new Error(`fetch ${src} -> HTTP ${resp.status}`);
        return await resp.text();
    }
    return fs.readFileSync(src, "utf8");
}

async function main() {
    const src = process.argv[2];
    if (!src) {
        console.error("usage: node update-pmdg-fields.js <path-or-url-to-PMDG_NG3_SDK.h>");
        process.exit(1);
    }

    const header = (await readSource(src)).replace(/\r\n/g, "\n");
    if (header.includes("</script")) {
        throw new Error("header contains </script and cannot be embedded verbatim");
    }
    if (!/struct\s+PMDG_NG3_Data/.test(header)) {
        console.warn("warning: no 'struct PMDG_NG3_Data' found - is this the NG3 SDK header?");
    }

    const htmlPath = path.join(__dirname, "pmdg-fields.html");
    const html = fs.readFileSync(htmlPath, "utf8");
    const begin = html.indexOf(BEGIN);
    const end = html.indexOf(END);
    if (begin < 0 || end < 0 || end < begin) throw new Error("markers not found in pmdg-fields.html");

    const block =
        BEGIN + "\n" +
        '    <script type="text/plain" id="sdk-header">' + header + "</script>\n" +
        END;

    fs.writeFileSync(htmlPath, html.slice(0, begin) + block + html.slice(end + END.length), "utf8");

    const structs = [...header.matchAll(/^struct\s+([A-Za-z_][A-Za-z0-9_]*)/gm)].map(m => m[1]);
    console.log(`embedded ${header.length} bytes from ${src}`);
    console.log(`structs found: ${structs.join(", ")}`);
}

main().catch(err => { console.error(err.message); process.exit(1); });
