// Links out of the dashboard go to the "Rig links" Chrome window on the same desktop (POST /api/open), because the
// dashboard runs as a Chrome --app window, which would otherwise hand them to whichever Chrome window was used last.
// One capture-phase listener covers every external link, Markdown and design-system buttons included. A modified
// click (new tab, new window, download) and any link the host cannot route keep the browser's own behaviour.

const isRoutable = (anchor: HTMLAnchorElement): boolean =>
  anchor.target === '_blank' && (anchor.protocol === 'http:' || anchor.protocol === 'https:') && !anchor.hasAttribute('download');

async function openInRigLinks(url: string): Promise<boolean> {
  try {
    const response = await fetch('/api/open', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url }),
    });
    return response.ok;
  } catch {
    return false;
  }
}

export function routeExternalLinks(target: Document = document): () => void {
  const onClick = (event: MouseEvent) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    const anchor = (event.target as Element | null)?.closest?.('a');
    if (!(anchor instanceof HTMLAnchorElement) || !isRoutable(anchor)) return;
    event.preventDefault();
    const url = anchor.href;
    void openInRigLinks(url).then((routed) => {
      if (!routed) window.open(url, '_blank', 'noopener,noreferrer');
    });
  };
  target.addEventListener('click', onClick, true);
  return () => target.removeEventListener('click', onClick, true);
}
