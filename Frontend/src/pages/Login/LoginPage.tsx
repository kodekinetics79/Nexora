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
  CircularProgress,
} from '@mui/material';
import {
  MailOutlined as MailIcon,
  LockOutlined as LockIcon,
  LightMode as SunIcon,
  DarkMode as MoonIcon,
  Visibility,
  VisibilityOff,
  CheckCircleOutlined as CheckIcon,
  VerifiedUserOutlined as IntegrityIcon,
  SettingsOutlined as SettingsIcon,
} from '@mui/icons-material';
import styled from '@emotion/styled';
import { useAuth } from '../../context/AuthContext';
import { useAppTheme } from '../../context/ThemeContext';
import Branding from '../../components/common/Branding';
import axiosInstance from '../../api/axiosInstance';
import { Link, useNavigate } from 'react-router-dom';
import userService, { type MePermissionsResponse } from '../../api/services/userService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { MAIN_CONTENT_ID } from '../../components/layout/SkipLink';
import { INBOX_ROOT } from '../../components/layout/navCatalog';

/** Ties the failure Alert to the fields it describes (SC 3.3.1 / SC 3.3.3). */
const LOGIN_ERROR_ID = 'login-error';

const Container = styled.div<{ mode: string }>`
  min-height: 100vh;
  min-height: 100dvh;
  width: 100%;
  box-sizing: border-box;
  background: ${props => props.mode === 'dark' ? '#07111f' : '#f5f7fa'};
  font-family: ${(props: any) => props.theme.typography.fontFamily};
  color-scheme: ${props => props.mode};
`;

const LoginShell = styled.div`
  min-height: 100vh;
  min-height: 100dvh;
  display: grid;
  grid-template-columns: minmax(420px, 44%) minmax(0, 56%);
  overflow-x: hidden;

  @media (max-width: 1024px) {
    grid-template-columns: 1fr;
    overflow: visible;
  }
`;

const EvidencePanel = styled.section`
  min-width: 0;
  padding: clamp(32px, 4vw, 64px);
  background: #08172a;
  color: #f8fafc;
  display: flex;
  flex-direction: column;
  border-right: 1px solid #283b58;

  @media (max-width: 1024px) {
    min-height: 252px;
    padding: 24px 32px;
    border-right: 0;
    border-bottom: 1px solid #283b58;
  }
  @media (max-width: 599.95px) {
    display: none;
  }
`;

const FormSection = styled.main<{ mode: string }>`
  min-width: 0;
  padding: clamp(32px, 5vw, 72px);
  background: ${props => props.mode === 'dark' ? '#0f1b2d' : '#f8f8f8'};
  color: ${props => props.mode === 'dark' ? '#f8fafc' : '#0b172a'};
  display: flex;
  flex-direction: column;
  justify-content: center;
  position: relative;

  @media (max-width: 1024px) {
    min-height: calc(100dvh - 252px);
  }

  @media (max-width: 599.95px) {
    min-height: auto;
    padding: 16px 20px 28px;
    justify-content: flex-start;
  }
`;

const StyledTextField = styled(TextField)<{ mode?: string }>(({ theme, mode }: any) => ({
  '& .MuiOutlinedInput-root': {
    minHeight: 58,
    backgroundColor: mode === 'dark' ? '#0b172a' : '#ffffff',
    borderRadius: 8,
    transition: 'border-color 160ms ease-out, box-shadow 160ms ease-out',
    '& fieldset': { borderColor: mode === 'dark' ? '#52627a' : '#aeb8c7' },
    '&:hover fieldset': { borderColor: mode === 'dark' ? '#8fa1b8' : '#65758b' },
    '&.Mui-focused': {
      boxShadow: `0 0 0 3px ${theme.palette.primary.main}24`,
    },
  },
  '& input': {
    '&::placeholder': {
      color: mode === 'dark' ? '#b7c4d6' : '#526174',
      opacity: 1,
    },
    '&:-webkit-autofill, &:-webkit-autofill:hover, &:-webkit-autofill:focus, &:-webkit-autofill:active': {
      WebkitBoxShadow: mode === 'dark' ? '0 0 0 30px #131c33 inset !important' : '0 0 0 30px #ffffff inset !important',
      WebkitTextFillColor: mode === 'dark' ? '#ffffff !important' : '#000000 !important',
      transition: 'background-color 5000s ease-in-out 0s',
      borderRadius: 0,
    }
  },
  '& .MuiInputLabel-root': {
    fontWeight: 600,
    color: mode === 'dark' ? '#b7c4d6' : '#506177',
  }
}));

const StyledSelect = styled(Select)<{ mode?: string }>(({ theme, mode }: any) => ({
  minHeight: 58,
  backgroundColor: mode === 'dark' ? '#0b172a' : '#ffffff',
  borderRadius: 8,
  transition: 'border-color 160ms ease-out, box-shadow 160ms ease-out',
  '& fieldset': { borderColor: mode === 'dark' ? '#52627a' : '#aeb8c7' },
  '&:hover fieldset': { borderColor: mode === 'dark' ? '#8fa1b8' : '#65758b' },
  '&.Mui-focused': {
    boxShadow: `0 0 0 3px ${theme.palette.primary.main}24`,
  }
}));

const evidenceStages = [
  { title: 'Capture & reconcile', detail: 'Customer email → Canonical Lead' },
  { title: 'Approve & source', detail: 'Participation → Formal RFQ' },
  { title: 'Fulfil & collect', detail: 'Order → Delivery evidence → Payment' },
];

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
  const { mode, setMode } = useAppTheme();
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
        hasModuleAuthorityByRank: me.hasModuleAuthorityByRank === true,
        roleId: me.roleId ?? data.roleId ?? undefined,
        businessUnitId: me.businessUnitId ?? data.businessUnitId ?? undefined,
        permissions: me.permissions ?? [],
        entitlements: me.entitlements ?? [],
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

  return (
    <Container mode={mode} data-testid="login-viewport">
      <LoginShell data-testid="login-card">
        <EvidencePanel aria-label="Nexora evidence-to-cash workflow">
          <Box
            sx={{
              '& .MuiTypography-root': { color: '#f8fafc !important' },
              '& img': { filter: 'brightness(0) invert(1)' },
              mb: { xs: 2, md: 6 },
            }}
          >
            <Branding fontSize={28} logoSize={38} showTagline={false} />
          </Box>

          <Typography
            component="p"
            sx={{
              maxWidth: 500,
              fontFamily: '"Cambay", "Source Sans 3", sans-serif',
              fontSize: { xs: 29, sm: 40, lg: 46 },
              fontWeight: 700,
              lineHeight: 1.08,
              letterSpacing: '-0.025em',
              color: '#f8fafc',
              mb: { xs: .75, md: 2 },
            }}
          >
            From source evidence to collected cash.
          </Typography>

          <Typography
            sx={{
              maxWidth: 500,
              color: '#b8c9de',
              fontSize: { xs: 14, sm: 18 },
              lineHeight: 1.55,
              mb: { xs: 0, sm: 4 },
            }}
          >
            Ownership, approvals and status stay attached at every handoff.
          </Typography>

          <Box
            aria-label="Illustrative commercial record"
            sx={{
              display: { xs: 'none', sm: 'flex' },
              alignItems: 'center',
              justifyContent: 'flex-start',
              gap: 2,
              py: 1.5,
              borderTop: '1px solid #35506f',
              borderBottom: '1px solid #35506f',
            }}
          >
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
              <IntegrityIcon aria-hidden="true" sx={{ color: '#20c7b5', fontSize: 22 }} />
              <Typography sx={{ color: '#dce5f0', fontSize: 14, fontWeight: 600 }}>
                Illustrative workflow · governed through payment
              </Typography>
            </Box>
          </Box>

          <Box
            component="ol"
            aria-label="Governed commercial stages"
            sx={{
              listStyle: 'none',
              p: 0,
              m: { xs: 0, sm: '28px 0 0' },
              display: 'grid',
              gridTemplateColumns: 'minmax(0, 1fr)',
              borderTop: '1px solid #283b58',
              borderLeft: '1px solid #283b58',
              '@media (max-width: 599.95px)': { display: 'none' },
            }}
          >
            {evidenceStages.map((stage) => (
              <Box
                component="li"
                key={stage.title}
                sx={{
                  minWidth: 0,
                  minHeight: 72,
                  px: 2,
                  py: 1.5,
                  display: 'grid',
                  gridTemplateColumns: '34px minmax(0, 1fr)',
                  alignItems: 'center',
                  gap: 1.5,
                  borderRight: '1px solid #283b58',
                  borderBottom: '1px solid #283b58',
                }}
              >
                <Box sx={{ width: 32, height: 32, border: '1px solid #20c7b5', borderRadius: '50%', display: 'grid', placeItems: 'center' }}>
                  <CheckIcon aria-hidden="true" sx={{ color: '#20c7b5', fontSize: 18 }} />
                </Box>
                <Box>
                  <Typography sx={{ color: '#f8fafc', fontSize: 14, fontWeight: 700, lineHeight: 1.2 }}>
                    {stage.title}
                  </Typography>
                  <Typography sx={{ color: '#9fb0c8', fontSize: 12, mt: .4, lineHeight: 1.2 }}>
                    {stage.detail}
                  </Typography>
                </Box>
              </Box>
            ))}
          </Box>

          <Box
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 1,
              mt: 'auto',
              pt: { xs: 2, sm: 4 },
              color: '#b8c9de',
            }}
          >
            <IntegrityIcon aria-hidden="true" sx={{ color: '#8fa6c3', fontSize: 18 }} />
            <Typography sx={{ color: '#b8c9de', fontSize: { xs: 11, sm: 12 }, lineHeight: 1.4 }}>
              Illustrative workflow · source, owner and timestamp retained at every transition
            </Typography>
          </Box>
        </EvidencePanel>

        <FormSection mode={mode} id={MAIN_CONTENT_ID} tabIndex={-1}>
          <Box
            sx={{
              display: { xs: 'block', sm: 'none' },
              maxWidth: 440,
              width: '100%',
              mx: 'auto',
              mb: 4,
              pr: 7,
            }}
          >
            <Branding fontSize={24} logoSize={34} showTagline={false} />
          </Box>

          <Box sx={{ position: 'absolute', top: { xs: 14, sm: 24 }, right: { xs: 14, sm: 24 } }}>
            <IconButton
              onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')}
              aria-label={mode === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
              sx={{ width: 50, height: 50, border: '1px solid', borderColor: mode === 'dark' ? '#52627a' : '#c7ced8', borderRadius: '7px' }}
            >
              {mode === 'dark' ? <SunIcon /> : <MoonIcon />}
            </IconButton>
          </Box>

          <Box sx={{ maxWidth: 440, width: '100%', mx: 'auto' }}>
            <Typography
              component="h1"
              sx={{
                fontFamily: '"Cambay", "Source Sans 3", sans-serif',
                fontSize: { xs: 36, md: 48 },
                fontWeight: 700,
                lineHeight: 1.1,
                letterSpacing: '-0.025em',
                mb: 1.5,
              }}
            >
              Sign in
            </Typography>
            <Typography variant="body1" sx={{ color: mode === 'dark' ? '#b7c4d6' : '#526174', mb: { xs: 3, sm: 4 }, lineHeight: 1.55, maxWidth: 420, fontSize: { sm: 17 } }}>
              Use your work account to access your Nexora workspace.
            </Typography>

            <Box component="form" onSubmit={handleLogin}>
              {notice && <Alert role="status" severity="info" sx={{ mb: 3, borderRadius: 2 }}>{notice}</Alert>}
              {businessUnitOptions ? (
                <>
                  <Alert severity="info" sx={{ mb: 3, borderRadius: 2 }}>
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
                    placeholder="you@example.com"
                    autoComplete="username"
                    inputMode="email"
                    label="Email address"
                    variant="outlined"
                    sx={{ mb: 3 }}
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    required
                    error={Boolean(error)}
                    slotProps={{
                      input: {
                        startAdornment: (
                          <InputAdornment position="start">
                            <MailIcon sx={{ color: 'text.secondary' }} />
                          </InputAdornment>
                        ),
                      },
                      htmlInput: {
                        inputMode: 'email',
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
                    sx={{ mb: 4 }}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    required
                    error={Boolean(error)}
                    slotProps={{
                      input: {
                        startAdornment: (
                          <InputAdornment position="start">
                            <LockIcon sx={{ color: 'text.secondary' }} />
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
                              sx={{ width: 44, height: 44 }}
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
                      sx={{ color: mode === 'dark' ? '#79b7ff' : '#075dcc', fontWeight: 600, fontSize: 16, textTransform: 'none' }}
                    >
                      Forgot password?
                    </Button>
                  </Box>
                </>
              )}

              {/* id lets the fields point at this message via
                  aria-describedby; role="alert" announces it on failure. */}
              {error && (
                <Alert id={LOGIN_ERROR_ID} role="alert" severity="error" sx={{ mb: 3, borderRadius: 2 }}>
                  {error}
                </Alert>
              )}

              <Button
                fullWidth
                variant="contained"
                size="large"
                type="submit"
                disabled={loading || (businessUnitOptions !== null && selectedBusinessUnitId === '')}
                aria-busy={loading}
                sx={{
                  minHeight: 61,
                  py: 1.5,
                  fontSize: 16,
                  borderRadius: '8px',
                  background: '#075dcc',
                  color: '#ffffff',
                  boxShadow: '0 10px 24px -16px rgba(9, 105, 232, .8)',
                  transition: 'background-color 160ms ease-out, box-shadow 160ms ease-out',
                  '&:hover': { background: '#064da9', boxShadow: '0 12px 26px -16px rgba(9, 105, 232, .9)' },
                  mt: 1,
                }}
              >
                {loading ? (
                  <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 1.25 }}>
                    <CircularProgress size={20} color="inherit" aria-hidden="true" />
                    Signing in…
                  </Box>
                ) : businessUnitOptions ? 'Continue' : 'Sign in'}
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
            </Box>

            {!businessUnitOptions && (
              <Box
                component="aside"
                aria-label="Platform administration sign-in"
                sx={{
                  mt: { xs: 3, sm: 4 },
                  pt: { xs: 2, sm: 2.5 },
                  borderTop: '1px solid',
                  borderColor: 'divider',
                  textAlign: 'center',
                }}
              >
                <Button
                  component={Link}
                  to="/platform/tenants"
                  variant="text"
                  size="small"
                  startIcon={<SettingsIcon aria-hidden="true" />}
                  sx={{ minHeight: 44, color: mode === 'dark' ? '#79b7ff' : '#075dcc', fontSize: 17, fontWeight: 600, textTransform: 'none' }}
                >
                  Platform administrator sign-in
                </Button>
              </Box>
            )}
          </Box>
        </FormSection>
      </LoginShell>
    </Container>
  );
};

export default LoginPage;
