import { Loader2 } from "lucide-react";
import { Navigate, Route, Routes, useParams } from "react-router-dom";
import { AppLayout } from "./components/AppLayout";
import { ProtectedRoute } from "./components/ProtectedRoute";
import { useAuth } from "./contexts/AuthContext";
import { AdminSettingsPage } from "./pages/AdminSettingsPage";
import { AdminUsersPage } from "./pages/AdminUsersPage";
import { BrowsePage } from "./pages/BrowsePage";
import { DashboardPage } from "./pages/DashboardPage";
import { EditorPage } from "./pages/EditorPage";
import { LoginPage } from "./pages/LoginPage";
import { NovelsPage } from "./pages/NovelsPage";
import { SetupPage } from "./pages/SetupPage";
import { SettingsPage } from "./pages/SettingsPage";
import { SourcesPage } from "./pages/SourcesPage";

export default function App() {
  const { loading, user, setupNeeded } = useAuth();

  if (loading)
    return (
      <div className="grid h-full place-items-center">
        <Loader2 className="h-5 w-5 animate-spin text-stone-400" />
      </div>
    );

  if (setupNeeded)
    return (
      <Routes>
        <Route path="/setup" element={<SetupPage />} />
        <Route path="*" element={<Navigate to="/setup" replace />} />
      </Routes>
    );

  return (
    <Routes>
      <Route path="/login" element={user ? <Navigate to="/" replace /> : <LoginPage />} />
      <Route element={<ProtectedRoute><AppLayout /></ProtectedRoute>}>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/novels" element={<NovelsPage />} />
        {/* Opening a novel in the library lands directly in the editor —
            it folds in the old detail surface (status, logs, action
            buttons). The legacy /editor sub-route is preserved as a redirect
            for bookmarks that pre-date this consolidation, plus /jobs is
            kept as a one-way redirect so old links still resolve. */}
        <Route path="/novels/:id" element={<EditorPage />} />
        <Route path="/novels/:id/editor" element={<RedirectToNovel />} />
        <Route path="/jobs" element={<Navigate to="/novels" replace />} />
        <Route path="/jobs/:id" element={<RedirectJobsToNovels />} />
        <Route path="/settings" element={<SettingsPage />} />
        <Route path="/sources" element={<SourcesPage />} />
        <Route path="/sources/:id/browse" element={<BrowsePage />} />
        <Route path="/admin/settings" element={<ProtectedRoute requireAdmin><AdminSettingsPage /></ProtectedRoute>} />
        <Route path="/admin/users" element={<ProtectedRoute requireAdmin><AdminUsersPage /></ProtectedRoute>} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

function RedirectToNovel() {
  const { id = "" } = useParams();
  return <Navigate to={`/novels/${id}`} replace />;
}

function RedirectJobsToNovels() {
  const { id = "" } = useParams();
  return <Navigate to={`/novels/${id}`} replace />;
}
