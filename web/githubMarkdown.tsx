// GitHub-flavoured extras that remark-gfm does not cover: alert blockquotes,
// Mermaid fences, and the sanitize schema that lets their markup through.
import { useEffect, useState } from 'react';
import { defaultSchema } from 'rehype-sanitize';

type Node = {
  type: string;
  tagName?: string;
  value?: string;
  properties?: Record<string, unknown>;
  children?: Node[];
  data?: object;
};

const ALERTS = {
  note: 'Note',
  tip: 'Tip',
  important: 'Important',
  warning: 'Warning',
  caution: 'Caution',
} as const;
type AlertKind = keyof typeof ALERTS;
const ALERT_MARKER = /^\[!(note|tip|important|warning|caution)\][ \t]*(?:\r?\n|$)/i;

function walk(node: Node, visit: (node: Node) => void) {
  visit(node);
  for (const child of node.children ?? []) walk(child, visit);
}

/** `> [!NOTE]` and friends become a titled alert box, as on github.com. */
export function remarkGithubAlerts() {
  return (tree: Node) =>
    walk(tree, (node) => {
      if (node.type !== 'blockquote') return;
      const paragraph = node.children?.[0];
      const text = paragraph?.type === 'paragraph' ? paragraph.children?.[0] : undefined;
      const match = text?.type === 'text' ? ALERT_MARKER.exec(text.value ?? '') : null;
      if (!paragraph || !text || !match) return;
      const kind = match[1]!.toLowerCase() as AlertKind;
      text.value = text.value!.slice(match[0].length);
      if (!text.value) paragraph.children!.shift();
      // A soft break straight after the marker leaves an empty leading break.
      if (paragraph.children![0]?.type === 'break') paragraph.children!.shift();
      if (!paragraph.children!.length) node.children!.shift();
      node.data = { hName: 'div', hProperties: { className: ['markdown-alert', `markdown-alert-${kind}`] } };
      node.children!.unshift({
        type: 'paragraph',
        data: { hProperties: { className: ['markdown-alert-title'] } },
        children: [{ type: 'text', value: ALERTS[kind] }],
      });
    });
}

/**
 * A linked image (Dependabot's compatibility badge) would render as a link inside
 * a link, because images render as links. Inside a link, keep only the alt text.
 */
export function rehypeUnwrapLinkedImages() {
  return (tree: Node) =>
    walk(tree, (node) => {
      if (node.type !== 'element' || node.tagName !== 'a') return;
      walk(node, (inner) => {
        inner.children = inner.children?.map((child) =>
          child.type === 'element' && child.tagName === 'img'
            ? { type: 'text', value: String(child.properties?.alt ?? '') }
            : child,
        );
      });
    });
}

/** GitHub's own allowlist, plus the classes the alert plugin adds. */
export const sanitizeSchema = {
  ...defaultSchema,
  attributes: {
    ...defaultSchema.attributes,
    div: [
      ...(defaultSchema.attributes?.div ?? []),
      ['className', 'markdown-alert', ...Object.keys(ALERTS).map((kind) => `markdown-alert-${kind}`)],
    ],
    p: [...(defaultSchema.attributes?.p ?? []), ['className', 'markdown-alert-title']],
  },
};

/** The text of a `pre > code.language-mermaid` block, or null for any other block. */
export function mermaidSource(pre: Node | undefined): string | null {
  const code = pre?.children?.find((child) => child.type === 'element' && child.tagName === 'code');
  const classes = code?.properties?.className;
  if (!Array.isArray(classes) || !classes.includes('language-mermaid')) return null;
  let text = '';
  walk(code!, (node) => {
    if (node.type === 'text') text += node.value ?? '';
  });
  return text;
}

declare global {
  interface Window {
    pele?: { render(source: string, options: Record<string, unknown>): { svg: string } };
  }
}

// Pele is already vendored for the C4 viewer; load it only when a diagram appears.
const PELE_URL = '/c4/static/vendor/pele.global.js';
let peleLoading: Promise<NonNullable<Window['pele']>> | null = null;
function loadPele() {
  peleLoading ??= new Promise((resolve, reject) => {
    if (window.pele) return resolve(window.pele);
    const script = document.createElement('script');
    script.src = PELE_URL;
    script.onload = () => (window.pele ? resolve(window.pele) : reject(new Error('Pele did not load')));
    script.onerror = () => {
      peleLoading = null;
      reject(new Error('Pele did not load'));
    };
    document.head.append(script);
  });
  return peleLoading;
}

// Pele's palette variables and the design-system tokens that stand in for them.
const PELE_TOKENS: Record<string, string> = {
  bg: '--ds-surface-base',
  fg: '--ds-text-primary',
  muted: '--ds-text-muted',
  line: '--ds-text-secondary',
  surface: '--ds-surface-raised',
  'surface-alt': '--ds-surface-sunken',
  border: '--ds-border-strong',
  accent: '--ds-accent-primary',
};

/**
 * An SVG shown as an image cannot read the page's CSS variables, so Pele's
 * `var(--pele-*, fallback)` colours are resolved to the current theme's tokens
 * before encoding. Anything unmapped keeps Pele's own fallback.
 */
function themed(svg: string) {
  const style = getComputedStyle(document.body);
  return svg.replace(/var\(--pele-([a-z0-9-]+),([^)]*)\)/g, (_, name: string, fallback: string) => {
    const token = PELE_TOKENS[name];
    return (token && style.getPropertyValue(token).trim()) || fallback;
  });
}

/**
 * Draws a Mermaid fence. The SVG comes from untrusted text, so it is shown as an
 * image: nothing inside an <img> SVG can run script or fetch anything. System
 * fonts keep Pele's text measurement in step with what the image draws.
 */
export function MermaidDiagram({ source, fallback }: { source: string; fallback: React.ReactNode }) {
  const [state, setState] = useState<{ src?: string; error?: string }>({});
  useEffect(() => {
    let current = true;
    loadPele()
      .then((pele) => {
        const { svg } = pele.render(source, { fontFamily: 'system-ui, sans-serif' });
        if (current) setState({ src: `data:image/svg+xml;charset=utf-8,${encodeURIComponent(themed(svg))}` });
      })
      .catch((error: unknown) => {
        if (current) setState({ error: error instanceof Error ? error.message : String(error) });
      });
    return () => {
      current = false;
    };
  }, [source]);
  if (!state.src)
    return (
      <>
        {state.error ? <p className="mermaid-error">Could not draw this diagram: {state.error}</p> : null}
        {fallback}
      </>
    );
  return (
    <figure className="mermaid-diagram">
      <img src={state.src} alt="Mermaid diagram" />
      <details>
        <summary>Diagram source</summary>
        {fallback}
      </details>
    </figure>
  );
}
