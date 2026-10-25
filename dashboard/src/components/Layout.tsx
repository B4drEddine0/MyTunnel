import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { useAuth } from "../auth";

const links = [
  { to: "/", label: "Overview", end: true },
  { to: "/tunnels", label: "Tunnels" },
  { to: "/projects", label: "Projects" },
  { to: "/tokens", label: "Access tokens" },
  { to: "/usage", label: "Usage" },
  { to: "/account", label: "Account" },
];

export function Layout() {
  const { user, signOut } = useAuth();
  const navigate = useNavigate();

  return (
    <div className="shell">
      <a className="skip-link" href="#main">Skip to content</a>
      <aside className="sidebar">
        <div className="brand">MyTunnel</div>
        <nav aria-label="Main">
          <ul>
            {links.map((link) => (
              <li key={link.to}>
                <NavLink to={link.to} end={link.end} className={({ isActive }) => (isActive ? "nav-link active" : "nav-link")}>
                  {link.label}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>
        <div className="sidebar-footer">
          <span className="muted small" title={user?.email}>{user?.email}</span>
          <button
            className="button button-small"
            onClick={async () => {
              await signOut();
              navigate("/login");
            }}
          >
            Sign out
          </button>
        </div>
      </aside>
      <main id="main" className="content">
        <Outlet />
      </main>
    </div>
  );
}
