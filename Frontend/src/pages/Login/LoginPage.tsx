import React, { useEffect, useState } from 'react';
import {
  Box,
  Typography,
  TextField,
  Button,
  IconButton,
  InputAdornment,
  Alert,
  Select,
  MenuItem,
  FormControl,
  InputLabel,
  useTheme,
  CircularProgress,
} from '@mui/material';
import {
  MailOutlined as MailIcon,
  LockOutlined as LockIcon,
  RocketLaunch as RocketIcon,
  Description as FileIcon,
  MonetizationOn as DollarIcon,
  Storage as DatabaseIcon,
  LocalShipping as TruckIcon,
  LightMode as SunIcon,
  DarkMode as MoonIcon,
  Visibility,
  VisibilityOff,
} from '@mui/icons-material';
import { keyframes, css } from '@emotion/react';
import styled from '@emotion/styled';
import { useAuth } from '../../context/AuthContext';
import { useAppTheme } from '../../context/ThemeContext';
import { contrastTextFor } from '../../utils/contrast';
import Branding from '../../components/common/Branding';
import axiosInstance from '../../api/axiosInstance';
import { Link, useNavigate } from 'react-router-dom';
import userService, { type MePermissionsResponse } from '../../api/services/userService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { MAIN_CONTENT_ID } from '../../components/layout/SkipLink';
import { INBOX_ROOT } from '../../components/layout/navCatalog';

/** Ties the failure Alert to the fields it describes (SC 3.3.1 / SC 3.3.3). */
const LOGIN_ERROR_ID = 'login-error';

// --- Animations ---
const gridFloat = keyframes`
  0% { transform: translateY(0); }
  100% { transform: translateY(-50px); }
`;

const scan = keyframes`
  0% { top: -10%; opacity: 0; }
  10%, 90% { opacity: 0.5; }
  100% { top: 110%; opacity: 0; }
`;

const spin = keyframes`
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
`;

const nodeLoop = (index: number) => keyframes`
  0%, ${index * 15}% { opacity: 0; transform: scale(0.8); }
  ${index * 15 + 5}%, 90% { opacity: 1; transform: scale(1); }
  95%, 100% { opacity: 0; transform: scale(0.8); }
`;

const pathLoop = (index: number) => keyframes`
  0%, ${index * 15 + 7}% { opacity: 0; stroke-dashoffset: 200; }
  ${index * 15 + 12}%, 90% { opacity: 0.7; stroke-dashoffset: 0; }
  95%, 100% { opacity: 0; stroke-dashoffset: 200; }
`;

// --- Styled Components ---
const Container = styled.div`
  min-height: 100vh;
  min-height: 100dvh;
  width: 100%;
  box-sizing: border-box;
  overflow-x: hidden;
  overflow-y: auto;
  padding: 24px;
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  background: ${(props: any) => props.theme.palette.background.default};
  font-family: ${(props: any) => props.theme.typography.fontFamily};
  transition: background 0.5s ease;

  @media (max-width: 768px) {
    align-items: stretch;
    padding: 0;
  }
`;

const ScannerLine = styled.div<{ primary: string }>`
  position: absolute;
  left: 0;
  right: 0;
  height: 2px;
  background: linear-gradient(90deg, transparent, ${props => props.primary}, transparent);
  box-shadow: 0 0 15px ${props => props.primary};
  z-index: 10;
  animation: ${scan} 4s linear infinite;
`;

const DigitalGrid = styled.div<{ primary: string }>`
  position: absolute;
  inset: -100px;
  background-image: 
    linear-gradient(${props => props.primary}1a 1px, transparent 1px),
    linear-gradient(90deg, ${props => props.primary}1a 1px, transparent 1px);
  background-size: 60px 60px;
  transform: perspective(1000px) rotateX(60deg);
  mask-image: radial-gradient(circle at center, black, transparent 80%);
  animation: ${gridFloat} 15s linear infinite;
  z-index: 0;
  opacity: 0.5;
`;

const GlassCard = styled.div<{ mode: string }>`
  width: 1400px;
  max-width: 95vw;
  min-height: min(850px, calc(100vh - 48px));
  min-height: min(850px, calc(100dvh - 48px));
  background: ${props => props.mode === 'dark' ? 'rgba(10, 15, 28, 0.85)' : 'rgba(255, 255, 255, 0.7)'};
  backdrop-filter: blur(40px);
  border-radius: 40px;
  border: 1px solid ${props => props.mode === 'dark' ? 'rgba(255, 255, 255, 0.08)' : 'rgba(0, 0, 0, 0.05)'};
  display: flex;
  overflow: hidden;
  position: relative;
  z-index: 10;
  box-shadow: ${props => props.mode === 'dark' ? '0 50px 120px -30px rgba(0, 0, 0, 0.9)' : '0 50px 120px -30px rgba(13, 71, 161, 0.15)'};
  transition: all 0.5s ease;

  @media (max-width: 1200px) {
    width: 100%;
    max-width: 680px;
    min-height: 0;
    flex-direction: column;
  }

  @media (max-width: 768px) {
    max-width: none;
    min-height: 100vh;
    min-height: 100dvh;
    border-radius: 0;
  }
`;

const VisualArea = styled.div<{ mode: string }>`
  flex: 1.8;
  background: ${props => props.mode === 'dark'
    ? 'linear-gradient(135deg, rgba(30, 41, 59, 0.4) 0%, rgba(15, 23, 42, 0.1) 100%)'
    : 'linear-gradient(135deg, rgba(241, 245, 249, 0.8) 0%, rgba(255, 255, 255, 0.2) 100%)'};
  border-right: 1px solid ${props => props.mode === 'dark' ? 'rgba(255, 255, 255, 0.05)' : 'rgba(0, 0, 0, 0.05)'};
  display: flex;
  flex-direction: column;
  padding: 48px;
  position: relative;
  justify-content: center;
  align-items: center;
  overflow: hidden;

  @media (max-width: 1200px) {
    display: none;
  }
`;

const FormSection = styled.div`
  flex: 1;
  padding: 64px;
  display: flex;
  flex-direction: column;
  justify-content: center;
  position: relative;
  background: transparent;

  @media (max-width: 768px) {
    padding: 24px;
  }
`;

const RadialWrapper = styled.div`
  position: relative;
  width: 580px;
  height: 580px;
  display: flex;
  justify-content: center;
  align-items: center;
  z-index: 5;
`;

const SensorRing = styled.div<{ color: string }>`
  position: absolute;
  inset: -12px;
  border: 1px dashed ${props => props.color};
  border-radius: 50%;
  opacity: 0.3;
  animation: ${spin} 15s linear infinite;
`;

const NodeBadge = styled.div<{ color: string }>`
  position: absolute;
  top: -10px;
  right: -10px;
  background: ${props => props.color};
  color: ${props => contrastTextFor(props.color)};
  font-size: 8px;
  padding: 2px 6px;
  border-radius: 4px;
  font-weight: 900;
  letter-spacing: 1px;
  box-shadow: 0 4px 10px rgba(0,0,0,0.3);
`;

const CircularStage = styled.div<{ index: number; x: number; y: number; color: string; mode: string }>`
  position: absolute;
  width: 110px;
  height: 110px;
  background: ${props => props.mode === 'dark' ? 'rgba(30, 41, 59, 1)' : 'rgba(255, 255, 255, 1)'};
  border: 2px solid ${props => props.color};
  border-radius: 50%;
  display: flex;
  flex-direction: column;
  justify-content: center;
  align-items: center;
  z-index: 20;
  left: ${props => props.x}px;
  top: ${props => props.y}px;
  margin-top: -55px;
  margin-left: -55px;
  opacity: 0;
  box-shadow: 0 12px 30px -10px rgba(0, 0, 0, 0.3);
  animation: ${props => css`${nodeLoop(props.index)} 10s infinite ease-in-out`};
  transition: transform 0.3s cubic-bezier(0.175, 0.885, 0.32, 1.275), box-shadow 0.3s ease;

  &:hover {
    transform: scale(1.1);
    box-shadow: 0 20px 40px -15px ${props => props.color}66;
  }
`;

const ConnectionLine = styled.line<{ index: number; color: string }>`
  stroke: ${props => props.color};
  stroke-width: 2;
  stroke-dasharray: 6 4;
  opacity: 0;
  stroke-linecap: round;
  animation: ${props => css`${pathLoop(props.index)} 10s infinite ease-in-out`};
`;

const SVGOverlay = styled.svg`
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  pointer-events: none;
  z-index: 5;
`;

const OrbitRing = styled.circle<{ primary: string }>`
  fill: none;
  stroke: ${props => props.primary}14;
  stroke-width: 1;
  stroke-dasharray: 4 4;
`;

// --- Custom Form Inputs ---
const StyledTextField = styled(TextField)<{ mode?: string }>(({ theme, mode }: any) => ({
  '& .MuiOutlinedInput-root': {
    backgroundColor: mode === 'dark' ? 'rgba(15, 23, 42, 0.6)' : 'rgba(255, 255, 255, 0.8)',
    borderRadius: '16px',
    transition: 'all 0.3s ease-in-out',
    boxShadow: mode === 'dark' ? '0 4px 20px rgba(0,0,0,0.2)' : '0 4px 20px rgba(13, 71, 161, 0.05)',
    border: '1px solid transparent',
    '& fieldset': { border: 'none' },
    '&:hover': {
      backgroundColor: mode === 'dark' ? 'rgba(15, 23, 42, 0.9)' : '#ffffff',
      boxShadow: mode === 'dark' ? '0 6px 24px rgba(0,0,0,0.4)' : '0 8px 24px rgba(13, 71, 161, 0.12)',
      transform: 'translateY(-2px)',
    },
    '&.Mui-focused': {
      backgroundColor: mode === 'dark' ? 'rgba(15, 23, 42, 1)' : '#ffffff',
      border: `1px solid ${theme.palette.primary.main}80`,
      boxShadow: `0 8px 32px ${theme.palette.primary.main}33`,
      transform: 'translateY(-2px)',
    }
  },
  '& input': {
    '&:-webkit-autofill, &:-webkit-autofill:hover, &:-webkit-autofill:focus, &:-webkit-autofill:active': {
      WebkitBoxShadow: mode === 'dark' ? '0 0 0 30px #131c33 inset !important' : '0 0 0 30px #ffffff inset !important',
      WebkitTextFillColor: mode === 'dark' ? '#ffffff !important' : '#000000 !important',
      transition: 'background-color 5000s ease-in-out 0s',
      borderRadius: '0px',
    }
  },
  '& .MuiInputLabel-root': {
    fontWeight: 500,
    '&.Mui-focused, &.MuiFormLabel-filled': {
      transform: 'translate(14px, -11px) scale(0.75)',
      backgroundColor: mode === 'dark' ? '#0a0f1c' : '#ffffff',
      padding: '0 6px',
      borderRadius: '4px',
    }
  }
}));

const StyledSelect = styled(Select)<{ mode?: string }>(({ theme, mode }: any) => ({
  backgroundColor: mode === 'dark' ? 'rgba(15, 23, 42, 0.6)' : 'rgba(255, 255, 255, 0.8)',
  borderRadius: '16px',
  transition: 'all 0.3s ease-in-out',
  boxShadow: mode === 'dark' ? '0 4px 20px rgba(0,0,0,0.2)' : '0 4px 20px rgba(13, 71, 161, 0.05)',
  border: '1px solid transparent',
  '& fieldset': { border: 'none' },
  '&:hover': {
    backgroundColor: mode === 'dark' ? 'rgba(15, 23, 42, 0.9)' : '#ffffff',
    boxShadow: mode === 'dark' ? '0 6px 24px rgba(0,0,0,0.4)' : '0 8px 24px rgba(13, 71, 161, 0.12)',
    transform: 'translateY(-2px)',
  },
  '&.Mui-focused': {
    backgroundColor: mode === 'dark' ? 'rgba(15, 23, 42, 1)' : '#ffffff',
    border: `1px solid ${theme.palette.primary.main}80`,
    boxShadow: `0 8px 32px ${theme.palette.primary.main}33`,
    transform: 'translateY(-2px)',
  }
}));

// --- Data ---
const industrialFlow = [
  { key: "DEMAND", title: "Smart Sourcing", icon: <RocketIcon />, color: "#6366f1" },
  { key: "ANALYZE", title: "AI Validation", icon: <FileIcon />, color: "#0ea5e9" },
  { key: "OPTIMIZE", title: "Dynamic Costing", icon: <DollarIcon />, color: "#10b981" },
  { key: "EXECUTE", title: "Global Fulfillment", icon: <DatabaseIcon />, color: "#f59e0b" },
  { key: "TRACK", title: "Real-time Logistics", icon: <TruckIcon />, color: "#8b5cf6" }
];

const SVG_SIZE = 580;
const CENTER = SVG_SIZE / 2;
const ORBIT_R = 215;
const NODE_R = 55;

const getNodeCenter = (index: number) => {
  const angleDeg = index * 72 - 90;
  const angleRad = angleDeg * (Math.PI / 180);
  return {
    x: CENTER + ORBIT_R * Math.cos(angleRad),
    y: CENTER + ORBIT_R * Math.sin(angleRad),
  };
};

const getEdgePoint = (from: { x: number; y: number }, to: { x: number; y: number }, radius: number) => {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const dist = Math.sqrt(dx * dx + dy * dy);
  return {
    x: from.x + (dx / dist) * radius,
    y: from.y + (dy / dist) * radius,
  };
};

// --- Auth service typing ---
interface LoginBusinessUnitOption {
  id: number;
  name: string;
}

interface LoginRequest {
  email: string;
  password: string;
  /** Only sent when disambiguating an email that exists in multiple organizations. */
  businessUnitId?: number;
}

interface LoginResponse {
  id: number;
  email: string;
  userName: string;
  roleId: number | null;
  roleName: string;
  isSuperAdmin: boolean;
  isManager: boolean;
  businessUnitId: number | null;
  businessUnitName: string | null;
  token: string;
  /** Set (with businessUnits, and no token) when the client must pick an organization. */
  requiresBusinessUnitSelection?: boolean;
  businessUnits?: LoginBusinessUnitOption[];
}

/**
 * Where a user lands after signing in.
 *
 * Everyone lands on the Inbox — the one screen that answers "what do I do next" instead of asking
 * the reader to choose a module.
 *
 * The history is worth keeping, because it is the reason this is a constant now. Login first called
 * `navigate('/analytics/deadlines')` unconditionally; that route is gated on Leads, and of the four
 * seeded starter roles only SALES_MANAGER and SALES_REP hold it, so a Procurement Officer signed in
 * successfully and landed on "Access Denied" as their first screen. The fix at the time was a
 * per-permission fallback table — seven candidate destinations, each with its own module gate. That
 * worked, but it meant four roles saw four different first screens, and none of the seven could show
 * work that had not yet become a lead.
 *
 * `/inbox` is deliberately UNGUARDED at the route and asks for each queue separately, so the
 * permission problem cannot recur: a user with no grants at all gets a page that names what is
 * missing and points at Setup, which is the correct answer to "I have no access", and a user with
 * some grants gets exactly the queues they can work. One first screen, for everybody.
 */
export const LANDING_ROUTE = INBOX_ROOT;

/**
 * Kept as a function, and still exported, because the sign-in flow and its tests both call it and
 * because the shape leaves room for a future per-role landing without moving the call site. It
 * ignores its arguments by design — see above: branching on permissions here is what produced the
 * Access Denied first screen.
 */
export const landingRouteFor = (
  _isSuperAdmin: boolean,
  _permissions: ReadonlyArray<{ moduleName: string; canView?: boolean }>,
): string => LANDING_ROUTE;

const LoginPage: React.FC = () => {
  const theme = useTheme();
  const { mode, setMode, primaryColor } = useAppTheme();
  const { setToken, setUserData } = useAuth();
  const navigate = useNavigate();

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [showPassword, setShowPassword] = useState(false);

  // FE-12: show a friendly message when we were redirected here because the
  // session expired (set by AuthContext), then clear it so it shows only once.
  useEffect(() => {
    const message = sessionStorage.getItem('authNotice');
    if (message) {
      setNotice(message);
      sessionStorage.removeItem('authNotice');
    }
  }, []);
  
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  // Rare case: the same email is valid in multiple organizations and the
  // server asks which one to sign in to.
  const [businessUnitOptions, setBusinessUnitOptions] = useState<LoginBusinessUnitOption[] | null>(null);
  const [selectedBusinessUnitId, setSelectedBusinessUnitId] = useState<number | ''>('');

  const submitLogin = async (businessUnitId?: number) => {
    setLoading(true);
    setError(null);
    try {
      const payload: LoginRequest = { email, password };
      if (businessUnitId !== undefined) {
        payload.businessUnitId = businessUnitId;
      }
      const response = await axiosInstance.post<LoginResponse>("/api/Auth/Login", payload);
      const data = response.data;

      if (data.requiresBusinessUnitSelection) {
        // No token yet — show the organization chooser and retry with the pick.
        setBusinessUnitOptions(data.businessUnits ?? []);
        setSelectedBusinessUnitId('');
        return;
      }

      setToken(data.token);

      // Bootstrap the caller's own grants.
      //
      // This used to read the whole role-permission table through an endpoint gated on
      // "Roles & Permissions: View". Any role without that specific module got a 403, the failure
      // was logged to the console and swallowed, and the user was navigated into an app with zero
      // permissions — empty sidebar, Access Denied everywhere, and nothing on screen explaining
      // why. `/api/User/me/permissions` needs authentication only, and a failure now BLOCKS the
      // login instead of producing a silently crippled session.
      let me: MePermissionsResponse;
      try {
        me = await userService.getMyPermissions();
      } catch (permErr) {
        // Roll the half-established session back so the user lands on a clean login screen
        // rather than a shell they cannot use.
        setToken(null);
        setError(
          presentableErrorMessage(
            permErr,
            'Signed in, but your permissions could not be loaded. Contact your administrator.',
          ),
        );
        return;
      }

      setUserData({
        id: me.userId ?? data.id,
        email: data.email,
        userName: data.userName,
        roleName: me.roleName ?? data.roleName,
        // Authority comes from the server's own RoleGate, not from a second derivation on the
        // login response — the two could disagree, rendering a UI every API call then rejects.
        isSuperAdmin: me.isSuperAdmin === true,
        isManager: me.isManager === true,
        roleId: me.roleId ?? data.roleId ?? undefined,
        businessUnitId: me.businessUnitId ?? data.businessUnitId ?? undefined,
        permissions: me.permissions ?? [],
      });
      navigate(landingRouteFor(me.isSuperAdmin === true, me.permissions ?? []));
    } catch (err: any) {
      setError(err.response?.data?.error || err.response?.data?.message || "Invalid credentials");
    } finally {
      setLoading(false);
    }
  };

  const handleLogin = (e: React.FormEvent) => {
    e.preventDefault();
    if (businessUnitOptions) {
      if (selectedBusinessUnitId === '') return;
      submitLogin(selectedBusinessUnitId);
    } else {
      submitLogin();
    }
  };

  const resetBusinessUnitSelection = () => {
    setBusinessUnitOptions(null);
    setSelectedBusinessUnitId('');
    setError(null);
  };

  const nodeCenters = industrialFlow.map((_, i) => getNodeCenter(i));

  // StyledTextField strips the outlined fieldset border, so MUI's default
  // `error` treatment is invisible. Restore a visible invalid state.
  const errorFieldSx = {
    '& .MuiOutlinedInput-root.Mui-error': {
      border: '1px solid',
      borderColor: 'error.main',
    },
  } as const;

  return (
    <Container theme={theme} data-testid="login-viewport">
      <DigitalGrid primary={primaryColor} />
      <ScannerLine primary={primaryColor} />

      <GlassCard mode={mode} data-testid="login-card">
        <VisualArea mode={mode}>
          {/* Purely decorative animated pipeline diagram — hidden from
              assistive tech so screen-reader users are not read ~15 orphan
              labels before reaching the sign-in form (SC 1.1.1). */}
          <RadialWrapper aria-hidden="true">
            <SVGOverlay viewBox={`0 0 ${SVG_SIZE} ${SVG_SIZE}`}>
              <defs>
                {industrialFlow.map((node, i) => (
                  <marker
                    key={`marker-${i}`}
                    id={`arrow-${i}`}
                    markerWidth="8"
                    markerHeight="8"
                    refX="4"
                    refY="3"
                    orient="auto"
                  >
                    <path fill={node.color} d="M0,0 L0,6 L7,3 z" style={{ opacity: 0.8 }} />
                  </marker>
                ))}
              </defs>

              <OrbitRing cx={CENTER} cy={CENTER} r={ORBIT_R} primary={primaryColor} />

              {industrialFlow.map((node, i) => {
                const nextIndex = (i + 1) % industrialFlow.length;
                const from = nodeCenters[i];
                const to = nodeCenters[nextIndex];
                const fromEdge = getEdgePoint(from, to, NODE_R + 4);
                const toEdge = getEdgePoint(to, from, NODE_R + 14);

                return (
                  <ConnectionLine
                    key={`conn-${i}`}
                    index={i}
                    x1={fromEdge.x}
                    y1={fromEdge.y}
                    x2={toEdge.x}
                    y2={toEdge.y}
                    color={node.color}
                    markerEnd={`url(#arrow-${i})`}
                  />
                );
              })}
            </SVGOverlay>

            {industrialFlow.map((node, i) => {
              const pos = nodeCenters[i];
              return (
                <CircularStage
                  key={node.key}
                  index={i}
                  x={pos.x}
                  y={pos.y}
                  color={node.color}
                  mode={mode}
                >
                  <SensorRing color={node.color} />
                  <NodeBadge color={node.color}>0{i + 1}</NodeBadge>
                  <Box sx={{ color: node.color, fontSize: 32, mb: 0.5, display: 'flex' }}>
                    {node.icon}
                  </Box>
                  <Typography variant="caption" sx={{ fontWeight: 800, color: node.color, textTransform: 'uppercase', letterSpacing: 1, fontSize: 10 }}>
                    {node.key}
                  </Typography>
                  <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.primary', opacity: 0.8, fontSize: 9 }}>
                    {node.title}
                  </Typography>
                </CircularStage>
              );
            })}
          </RadialWrapper>

          <Box
            component="aside"
            aria-label="Nexora inquiry-to-RFQ workflow"
            sx={{
              position: 'absolute',
              left: '50%',
              top: '50%',
              transform: 'translate(-50%, -50%)',
              zIndex: 15,
              width: 270,
              px: 2.5,
              py: 2.25,
              textAlign: 'center',
              borderRadius: 4,
              border: '1px solid',
              borderColor: 'divider',
              bgcolor: mode === 'dark' ? 'rgba(15, 23, 42, 0.9)' : 'rgba(255, 255, 255, 0.92)',
              backdropFilter: 'blur(14px)',
              boxShadow: mode === 'dark'
                ? '0 18px 50px -28px rgba(0, 0, 0, 0.95)'
                : '0 18px 50px -28px rgba(15, 23, 42, 0.35)',
            }}
          >
            <Typography component="p" variant="h5" sx={{ fontWeight: 900, lineHeight: 1.15, mb: 1 }}>
              Email evidence to governed RFQ
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ lineHeight: 1.55 }}>
              Capture. Reconcile. Decide. Promote. Every approved line stays traceable to its source.
            </Typography>
          </Box>

          <Box sx={{ position: 'absolute', top: 50, left: 50, zIndex: 30 }}>
            <Branding fontSize={28} />
          </Box>
        </VisualArea>

        <FormSection as="main" id={MAIN_CONTENT_ID} tabIndex={-1}>
          <Box sx={{ position: 'absolute', top: 40, right: 40, display: 'flex', gap: 2 }}>
            <IconButton
              onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')}
              aria-label={mode === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            >
              {mode === 'dark' ? <SunIcon /> : <MoonIcon />}
            </IconButton>
          </Box>

          <Box sx={{ maxWidth: 400, width: '100%', mx: 'auto' }}>
            {/* variant keeps the visual scale; component makes it the page's
                only <h1> so the document has a heading structure (SC 1.3.1). */}
            <Typography variant="h3" component="h1" sx={{ fontWeight: 900, mb: 1, letterSpacing: '-0.03em' }}>
              Welcome back
            </Typography>
            <Typography variant="body1" sx={{ color: 'text.secondary', mb: 6 }}>
              Enter your operational credentials to continue.
            </Typography>

            {notice && <Alert role="status" severity="info" sx={{ mb: 3, borderRadius: 3 }}>{notice}</Alert>}

            <form onSubmit={handleLogin}>
              {businessUnitOptions ? (
                <>
                  <Alert severity="info" sx={{ mb: 3, borderRadius: 3 }}>
                    Your account belongs to more than one organization. Pick one to continue.
                  </Alert>
                  <FormControl fullWidth sx={{ mb: 4 }}>
                    <InputLabel id="bu-label">Which organization?</InputLabel>
                    <StyledSelect
                      mode={mode}
                      id="business-unit"
                      name="businessUnitId"
                      labelId="bu-label"
                      label="Which organization?"
                      value={selectedBusinessUnitId}
                      onChange={(e) => setSelectedBusinessUnitId(Number(e.target.value))}
                      required
                      error={Boolean(error)}
                      aria-describedby={error ? LOGIN_ERROR_ID : undefined}
                    >
                      {businessUnitOptions.map((bu) => (
                        <MenuItem key={bu.id} value={bu.id}>
                          {bu.name}
                        </MenuItem>
                      ))}
                    </StyledSelect>
                  </FormControl>
                </>
              ) : (
                <>
                  <StyledTextField
                    mode={mode}
                    fullWidth
                    id="email"
                    name="email"
                    // type=email gets the right mobile keyboard and native
                    // validation; autoComplete lets password managers and
                    // browser autofill work (SC 1.3.5 Identify Input Purpose).
                    type="email"
                    autoComplete="username"
                    inputMode="email"
                    label="Email Address"
                    variant="outlined"
                    sx={{ mb: 3, ...errorFieldSx }}
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    required
                    error={Boolean(error)}
                    slotProps={{
                      input: {
                        startAdornment: (
                          <InputAdornment position="start">
                            <MailIcon sx={{ color: 'primary.main', opacity: 0.7 }} />
                          </InputAdornment>
                        ),
                      },
                      htmlInput: {
                        'aria-invalid': Boolean(error),
                        'aria-describedby': error ? LOGIN_ERROR_ID : undefined,
                      },
                    }}
                  />

                  <StyledTextField
                    mode={mode}
                    fullWidth
                    id="password"
                    name="password"
                    autoComplete="current-password"
                    label="Password"
                    type={showPassword ? 'text' : 'password'}
                    variant="outlined"
                    sx={{ mb: 4, ...errorFieldSx }}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    required
                    error={Boolean(error)}
                    slotProps={{
                      input: {
                        startAdornment: (
                          <InputAdornment position="start">
                            <LockIcon sx={{ color: 'primary.main', opacity: 0.7 }} />
                          </InputAdornment>
                        ),
                        endAdornment: (
                          <InputAdornment position="end">
                            <IconButton
                              onClick={() => setShowPassword(!showPassword)}
                              edge="end"
                              // Icon-only control: needs a name, and the toggle
                              // state has to be exposed, not just implied by
                              // which icon is drawn (SC 4.1.2).
                              aria-label={showPassword ? 'Hide password' : 'Show password'}
                              aria-pressed={showPassword}
                            >
                              {showPassword ? <VisibilityOff /> : <Visibility />}
                            </IconButton>
                          </InputAdornment>
                        ),
                      },
                      htmlInput: {
                        'aria-invalid': Boolean(error),
                        'aria-describedby': error ? LOGIN_ERROR_ID : undefined,
                      },
                    }}
                  />

                  {/* The way out for somebody who cannot get in.
                      Rendered only on the credentials step: the business-unit
                      selection below appears AFTER the password has already been
                      verified, and offering "forgot password" to someone who
                      just proved they remember it is noise.
                      Until this page had a link, the only recovery route was for
                      an operator to overwrite the hash in the database — and
                      ActivateAccountPage has been telling users to "use forgot
                      password on the sign-in page" the whole time. */}
                  <Box sx={{ mt: -3, mb: 3, display: 'flex', justifyContent: 'flex-end' }}>
                    <Button
                      component={Link}
                      to="/forgot-password"
                      variant="text"
                      size="small"
                      sx={{ fontWeight: 700, textTransform: 'none' }}
                    >
                      Forgot password?
                    </Button>
                  </Box>
                </>
              )}

              {/* id lets the fields point at this message via
                  aria-describedby; role="alert" announces it on failure. */}
              {error && (
                <Alert id={LOGIN_ERROR_ID} role="alert" severity="error" sx={{ mb: 3, borderRadius: 3 }}>
                  {error}
                </Alert>
              )}

              <Button
                fullWidth
                variant="contained"
                size="large"
                type="submit"
                disabled={loading || (businessUnitOptions !== null && selectedBusinessUnitId === '')}
                sx={{
                  py: 2,
                  fontSize: 16,
                  boxShadow: `0 12px 30px -10px ${primaryColor}66`,
                  '&:hover': { transform: 'translateY(-2px)' },
                  transition: 'all 0.3s ease',
                }}
              >
                {loading
                  ? <CircularProgress size={24} color="inherit" />
                  : businessUnitOptions ? 'CONTINUE' : 'LOGIN'}
              </Button>

              {businessUnitOptions && (
                <Button
                  fullWidth
                  variant="text"
                  onClick={resetBusinessUnitSelection}
                  disabled={loading}
                  sx={{ mt: 2 }}
                >
                  Back to sign in
                </Button>
              )}
            </form>

            {!businessUnitOptions && (
              <Box
                component="aside"
                aria-label="Platform administration sign-in"
                sx={{ mt: 3, pt: 2.5, borderTop: '1px solid', borderColor: 'divider', textAlign: 'center' }}
              >
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.75 }}>
                  Platform Owners use a separate control-plane account for cross-tenant administration.
                </Typography>
                <Button
                  component={Link}
                  to="/platform/tenants"
                  variant="text"
                  size="small"
                  sx={{ fontWeight: 800, textTransform: 'none' }}
                >
                  Platform Owner? Manage or delete tenants
                </Button>
              </Box>
            )}
          </Box>
        </FormSection>
      </GlassCard>
    </Container>
  );
};

export default LoginPage;
