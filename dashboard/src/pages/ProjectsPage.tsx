import { useState, type FormEvent } from "react";
import { api, ApiError, type Project } from "../api";
import { Card, CopyButton, Empty, ErrorBanner, Field, Loading, PageHeader, StatusBadge, useAsync } from "../components/ui";

const subdomainPattern = /^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$/;

function ProjectCard({ project, onChanged }: { project: Project; onChanged: () => Promise<void> }) {
  const [subdomain, setSubdomain] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function reserve(event: FormEvent) {
    event.preventDefault();
    const value = subdomain.trim().toLowerCase();
    if (!subdomainPattern.test(value)) {
      setError("Use 3-63 lowercase letters, digits or hyphens, not starting or ending with a hyphen.");
      return;
    }
    try {
      await api.reserveSubdomain(project.id, value);
      setSubdomain("");
      setError(null);
      await onChanged();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Could not reserve the subdomain.");
    }
  }

  async function act(action: () => Promise<void>, confirmText: string) {
    if (!confirm(confirmText)) return;
    try {
      await action();
      await onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : "The action failed.");
    }
  }

  return (
    <Card
      title={project.name}
      actions={
        <button className="button button-small button-danger" onClick={() => act(() => api.deleteProject(project.id), `Delete project "${project.name}" and release its subdomains?`)}>
          Delete
        </button>
      }
    >
      {project.subdomains.length === 0 ? (
        <p className="muted">No reserved subdomains. Reserve one so your URL stays the same and nobody else can use it.</p>
      ) : (
        <ul className="list">
          {project.subdomains.map((s) => (
            <li key={s.id} className="list-row">
              <span className="grow">
                <a href={s.publicUrl} target="_blank" rel="noopener noreferrer">{s.publicUrl}</a>
                <span className="muted small"> · <code>mytunnel http 3000 --subdomain {s.subdomain}</code></span>
              </span>
              <StatusBadge online={s.online} />
              <CopyButton value={`mytunnel http 3000 --subdomain ${s.subdomain}`} label="Copy command" />
              <button className="button button-small" onClick={() => act(() => api.releaseSubdomain(project.id, s.id), `Release ${s.subdomain}? Anyone could then claim it.`)}>
                Release
              </button>
            </li>
          ))}
        </ul>
      )}
      <form onSubmit={reserve} className="inline-form">
        <Field label="Reserve a subdomain" error={error ?? undefined}>
          <input value={subdomain} maxLength={63} placeholder="my-app" onChange={(e) => setSubdomain(e.target.value)} />
        </Field>
        <button className="button" disabled={!subdomain.trim()}>Reserve</button>
      </form>
    </Card>
  );
}

export function ProjectsPage() {
  const { data, error, loading, reload } = useAsync(api.projects);
  const [name, setName] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  async function create(event: FormEvent) {
    event.preventDefault();
    try {
      await api.createProject(name.trim());
      setName("");
      setFormError(null);
      await reload();
    } catch (e) {
      setFormError(e instanceof ApiError ? e.message : "Could not create the project.");
    }
  }

  return (
    <>
      <PageHeader title="Projects" description="Group your reserved subdomains by project." />
      <Card title="New project">
        <form onSubmit={create} className="inline-form">
          <Field label="Name" error={formError ?? undefined}>
            <input required maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <button className="button button-primary" disabled={!name.trim()}>Create project</button>
        </form>
      </Card>
      {error ? <ErrorBanner message={error} onRetry={reload} /> : !data && loading ? <Loading /> : data!.length === 0 ? (
        <Card><Empty title="No projects yet">Create a project to reserve subdomains.</Empty></Card>
      ) : (
        data!.map((p) => <ProjectCard key={p.id} project={p} onChanged={reload} />)
      )}
    </>
  );
}
