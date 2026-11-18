import { Prose } from '@rtkelly-labs/design-system';
import { useId, useMemo } from 'react';
import Markdown, { defaultUrlTransform } from 'react-markdown';
import rehypeRaw from 'rehype-raw';
import rehypeSanitize from 'rehype-sanitize';
import remarkGemoji from 'remark-gemoji';
import remarkGfm from 'remark-gfm';
import {
  MermaidDiagram,
  mermaidSource,
  rehypeUnwrapLinkedImages,
  remarkGithubAlerts,
  sanitizeSchema,
} from './githubMarkdown';

/**
 * GitHub text is untrusted. Bodies such as Dependabot's are mostly inline HTML
 * (<details>, <blockquote>, <ul>), so raw HTML is parsed and then sanitized with
 * GitHub's own allowlist (rehype-sanitize's default schema); sanitize must run
 * after rehype-raw. URL sanitization stays on.
 */
export function MarkdownContent({ children, sourceUrl }: { children: string; sourceUrl: string }) {
  // Several bodies share the page (the description and every comment), so each
  // gets its own id prefix or two `[^1]` footnotes would collide.
  const prefix = `user-content-${useId().replace(/[^\w-]/g, '')}-`;
  const schema = useMemo(() => ({ ...sanitizeSchema, clobberPrefix: prefix }), [prefix]);
  return (
    <Prose className="triage-prose">
      <Markdown
        remarkPlugins={[remarkGfm, remarkGemoji, remarkGithubAlerts]}
        rehypePlugins={[rehypeRaw, [rehypeSanitize, schema], rehypeUnwrapLinkedImages]}
        // Sanitize adds the prefix to every id; adding one here too would double it.
        remarkRehypeOptions={{ clobberPrefix: '' }}
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
          a: ({ children, href, id, node }) => {
            // Footnote links point into this page, at the ids sanitize prefixed, not at github.com.
            const properties = node?.properties ?? {};
            if (href && ('dataFootnoteRef' in properties || 'dataFootnoteBackref' in properties))
              return (
                <a id={id} href={`#${prefix}${new URL(href).hash.slice(1)}`}>
                  {children}
                </a>
              );
            return (
              <a href={href} target="_blank" rel="noopener noreferrer">
                {children}
              </a>
            );
          },
          img: ({ alt, src }) => (
            <a
              href={typeof src === 'string' ? src : undefined}
              target="_blank"
              rel="noopener noreferrer"
            >
              {alt || 'View attached image'}
            </a>
          ),
          pre: ({ children, node }) => {
            const block = <pre>{children}</pre>;
            const source = mermaidSource(node);
            return source == null ? block : <MermaidDiagram source={source} fallback={block} />;
          },
        }}
      >
        {children}
      </Markdown>
    </Prose>
  );
}
