import { Prose } from '@rtkelly-labs/design-system';
import Markdown, { defaultUrlTransform } from 'react-markdown';
import remarkGfm from 'remark-gfm';

/** GitHub text is untrusted: raw HTML stays disabled and URL sanitization stays on. */
export function MarkdownContent({ children, sourceUrl }: { children: string; sourceUrl: string }) {
  return (
    <Prose className="triage-prose">
      <Markdown
        remarkPlugins={[remarkGfm]}
        urlTransform={(url) => {
          const safe = defaultUrlTransform(url);
          if (!safe) return '';
          try {
            return new URL(safe, sourceUrl).href;
          } catch {
            return '';
          }
        }}
        components={{
          a: ({ children, href }) => (
            <a href={href} target="_blank" rel="noopener noreferrer">
              {children}
            </a>
          ),
          img: ({ alt, src }) => (
            <a
              href={typeof src === 'string' ? src : undefined}
              target="_blank"
              rel="noopener noreferrer"
            >
              {alt || 'View attached image'}
            </a>
          ),
        }}
      >
        {children}
      </Markdown>
    </Prose>
  );
}
