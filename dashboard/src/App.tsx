import { Navigate, Route, Routes, useLocation } from "react-router-dom";
import type { ReactNode } from "react";
import { useAuth } from "./auth";
import { Layout } from "./components/Layout";
import { Loading } from "./components/ui";
import { LoginPage, RegisterPage } from "./pages/AuthPages";
import { OverviewPage } from "./pages/OverviewPage";
import { TunnelsPage } from "./pages/TunnelsPage";
import { ProjectsPage } from "./pages/ProjectsPage";
import { TokensPage } from "./pages/TokensPage";
import { UsagePage } from "./pages/UsagePage";
import { AccountPage } from "./pages/AccountPage";
import { ActivatePage } from "./pages/ActivatePage";

function RequireAuth({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth();
  const location = useLocation();
  if (loading) return <div className="centered"><Loading /></div>;
  if (!user) return <Navigate to={`/login?next=${encodeURIComponent(location.pathname + location.search)}`} replace />;
  return <>{children}</>;
}

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route element={<RequireAuth><Layout /></RequireAuth>}>
        <Route index element={<OverviewPage />} />
        <Route path="tunnels" element={<TunnelsPage />} />
        <Route path="projects" element={<ProjectsPage />} />
        <Route path="tokens" element={<TokensPage />} />
        <Route path="usage" element={<UsagePage />} />
        <Route path="account" element={<AccountPage />} />
        <Route path="activate" element={<ActivatePage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
