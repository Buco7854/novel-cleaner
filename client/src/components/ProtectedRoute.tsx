import { Navigate, Outlet } from "react-router-dom";
import { useAuth } from "../contexts/AuthContext";

interface Props {
  requireAdmin?: boolean;
  children?: React.ReactNode;
}

export function ProtectedRoute({ requireAdmin, children }: Props) {
  const { user, isAdmin } = useAuth();
  if (!user) return <Navigate to="/login" replace />;
  if (requireAdmin && !isAdmin) return <Navigate to="/" replace />;
  return children ? <>{children}</> : <Outlet />;
}
