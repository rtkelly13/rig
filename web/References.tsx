import { Badge, Input } from '@rtkelly-labs/design-system';
import { references } from './referenceCatalog';
import { updateQuery } from './router';
import type { Route } from './router';
import { Icon } from './Icon';

export function References({ route }: { route: Route }) {
  const search = route.query.get('q') || '';
  const category = route.query.get('category') || '';
  const visible = references.filter(
    (reference) =>
      (!category || reference.category === category) &&
      `${reference.name} ${reference.pattern} ${reference.adaptation}`
        .toLowerCase()
        .includes(search.toLowerCase()),
  );
  return (
    <section className="reference-page" aria-labelledby="reference-title">
      <div className="page-heading">
        <div>
          <span className="eyebrow">Design library</span>
          <h1 id="reference-title">Triage interface catalogue</h1>
          <p>Inbox and review interfaces: patterns to study and apply in rig.</p>
        </div>
        <Badge accent="quiet">{references.length} references</Badge>
      </div>
      <div className="reference-intro">
        <Icon name="book" />
        <p>
          Research informs the workflow. Rig keeps its own design-system language and its explicit,
          commit-bound review process.
        </p>
        <span>Reviewed 29 Oct 2026</span>
      </div>
      <div className="catalogue-tools">
        <Input
          aria-label="Search references"
          placeholder="Search interfaces or patterns…"
          value={search}
          onChange={(event) => updateQuery('q', event.target.value, true)}
        />
        <label className="select-field">
          <span>Category</span>
          <select value={category} onChange={(event) => updateQuery('category', event.target.value)}>
            <option value="">All patterns</option>
            <option>Inbox</option>
            <option>Review</option>
            <option>Notifications</option>
          </select>
        </label>
      </div>
      <div className="reference-grid">
        {visible.map((reference) => (
          <article className="reference-card" key={reference.name}>
            <div className={`pattern-preview pattern-${reference.layout}`} aria-hidden="true">
              <div className="pattern-nav">
                <i />
                <i />
                <i />
                <i />
              </div>
              <div className="pattern-inbox">
                <b />
                <i />
                <i />
                <b />
                <i />
                <i />
              </div>
              <div className="pattern-detail">
                <b />
                <i />
                <i />
                <div />
                <i />
              </div>
            </div>
            <div className="reference-body">
              <div className="reference-title">
                <h2>{reference.name}</h2>
                <Badge accent="quiet">{reference.category}</Badge>
              </div>
              <p>{reference.evidence}</p>
              <dl>
                <dt>Pattern to study</dt>
                <dd>{reference.pattern}</dd>
                <dt>Apply in rig</dt>
                <dd>{reference.adaptation}</dd>
                <dt>Keep in mind</dt>
                <dd>{reference.boundary}</dd>
              </dl>
              <a href={reference.url} target="_blank" rel="noopener noreferrer">
                View official reference <Icon name="external" />
              </a>
              {reference.screenshot ? (
                <a
                  className="related-reference"
                  href={reference.screenshot}
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  View UI capture <Icon name="external" />
                </a>
              ) : null}
              {reference.related ? (
                <a
                  className="related-reference"
                  href={reference.related}
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  Snooze reference <Icon name="external" />
                </a>
              ) : null}
            </div>
          </article>
        ))}
      </div>
      {!visible.length && (
        <p className="empty-state">No references match these filters. Try another pattern or category.</p>
      )}
      <p className="reference-footnote">
        Diagrams are original schematic studies, not product screenshots. Sources link to the official product
        documentation and UI examples.
      </p>
    </section>
  );
}
