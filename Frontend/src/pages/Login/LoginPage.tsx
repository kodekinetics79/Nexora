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
  VerifiedUserOutlined as IntegrityIcon,
  SettingsOutlined as SettingsIcon,
  ArrowForwardRounded as ArrowIcon,
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
import { loginErrorMessage } from './loginError';
import { BrandHero, EvidenceSpine } from './EvidenceSpine';

/** Ties the failure Alert to the fields it describes (SC 3.3.1 / SC 3.3.3). */
const LOGIN_ERROR_ID = 'login-error';

const Container = styled.div<{ mode: string }>`
  min-height: 100vh;
  min-height: 100dvh;
  width: 100%;
  box-sizing: border-box;
  background: ${props => props.mode === 'dark' ? '#121418' : '#f5f4f1'};
  font-family: ${(props: any) => props.theme.typography.fontFamily};
  color-scheme: ${props => props.mode};

  ::selection {
    background: ${props => props.mode === 'dark' ? '#e0a100' : '#c9931a'};
    color: #ffffff;
  }
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
  background:
    radial-gradient(circle at 18% 12%, rgba(201, 147, 26, .27), transparent 34%),
    radial-gradient(circle at 88% 82%, rgba(224, 161, 0, .16), transparent 33%),
    linear-gradient(145deg, #101317 0%, #14171d 54%, #0e1013 100%);
  color: #f8fafc;
  display: flex;
  flex-direction: column;
  border-right: 1px solid #2a2f3a;
  position: relative;
  overflow: hidden;
  isolation: isolate;

  @media (max-width: 1024px) {
    min-height: 252px;
    padding: 24px 32px;
    border-right: 0;
    border-bottom: 1px solid #2a2f3a;
  }
  @media (max-width: 599.95px) {
    display: none;
  }
`;

const FormSection = styled.main<{ mode: string }>`
  min-width: 0;
  padding: clamp(32px, 5vw, 72px);
  background: ${props => props.mode === 'dark'
    ? 'radial-gradient(circle at 85% 18%, rgba(201, 147, 26, .17), transparent 30%), radial-gradient(circle at 14% 88%, rgba(224, 161, 0, .09), transparent 28%), #101317'
    : 'radial-gradient(circle at 88% 15%, rgba(224, 161, 0, .14), transparent 32%), radial-gradient(circle at 10% 88%, rgba(224, 161, 0, .10), transparent 28%), linear-gradient(145deg, #f7f6f2 0%, #f1efe9 100%)'};
  color: ${props => props.mode === 'dark' ? '#f8fafc' : '#171a20'};
  display: flex;
  flex-direction: column;
  justify-content: center;
  position: relative;
  overflow: hidden;

  @media (max-width: 1024px) {
    min-height: calc(100dvh - 252px);
  }

  @media (max-width: 599.95px) {
    min-height: auto;
    padding: 16px 20px 28px;
    justify-content: flex-start;
  }
`;

const AuthSurface = styled.div<{ mode: string }>`
  position: relative;
  z-index: 1;
  max-width: 472px;
  width: 100%;
  margin: 0 auto;
  padding: clamp(32px, 3.4vw, 46px);
  border: 1px solid ${props => props.mode === 'dark' ? 'rgba(170, 176, 190, .24)' : 'rgba(95, 102, 115, .20)'};
  border-radius: 22px;
  background: ${props => props.mode === 'dark' ? 'rgba(18, 20, 24, .86)' : 'rgba(255, 255, 255, .86)'};
  box-shadow: ${props => props.mode === 'dark'
    ? '0 28px 70px -32px rgba(0, 0, 0, .82), 0 10px 30px -24px rgba(224, 161, 0, .32)'
    : '0 30px 72px -34px rgba(15, 18, 24, .36), 0 12px 32px -26px rgba(201, 147, 26, .34)'};
  backdrop-filter: blur(18px) saturate(118%);

  &::before {
    content: '';
    position: absolute;
    top: -1px;
    left: 38px;
    right: 38px;
    height: 1px;
    background: linear-gradient(90deg, transparent, rgba(243, 210, 122, .7), rgba(224, 161, 0, .72), transparent);
  }

  @media (max-width: 599.95px) {
    max-width: 440px;
    padding: 0;
    border: 0;
    border-radius: 0;
    background: transparent;
    box-shadow: none;
    backdrop-filter: none;

    &::before { display: none; }
  }
`;

const StyledTextField = styled(TextField)<{ mode?: string }>(({ theme, mode }: any) => ({
  '& .MuiOutlinedInput-root': {
    minHeight: 58,
    backgroundColor: mode === 'dark' ? 'rgba(23, 26, 32, .92)' : 'rgba(255, 255, 255, .94)',
    borderRadius: 12,
    boxShadow: mode === 'dark' ? '0 8px 24px -22px rgba(0, 0, 0, .9)' : '0 10px 26px -24px rgba(42, 47, 58, .55)',
    transition: 'border-color 180ms ease-out, box-shadow 180ms ease-out, background-color 180ms ease-out',
    '& fieldset': { borderColor: mode === 'dark' ? '#5f6673' : '#b9bcc4' },
    '&:hover fieldset': { borderColor: mode === 'dark' ? '#a3a9b5' : '#6b7280' },
    '&.Mui-focused': {
      backgroundColor: mode === 'dark' ? '#171a20' : '#ffffff',
      boxShadow: `0 0 0 3px ${theme.palette.primary.main}24, 0 14px 30px -24px ${theme.palette.primary.main}99`,
    },
  },
  '& input': {
    '&::placeholder': {
      color: mode === 'dark' ? '#cbc7bc' : '#5f6673',
      opacity: 1,
    },
    '&:-webkit-autofill, &:-webkit-autofill:hover, &:-webkit-autofill:focus, &:-webkit-autofill:active': {
      WebkitBoxShadow: mode === 'dark' ? '0 0 0 30px #1b1f26 inset !important' : '0 0 0 30px #ffffff inset !important',
      WebkitTextFillColor: mode === 'dark' ? '#ffffff !important' : '#000000 !important',
      transition: 'background-color 5000s ease-in-out 0s',
      borderRadius: 0,
    }
  },
  '& .MuiInputLabel-root': {
    fontWeight: 600,
    color: mode === 'dark' ? '#cbc7bc' : '#5f6673',
  }
}));

const StyledSelect = styled(Select)<{ mode?: string }>(({ theme, mode }: any) => ({
  minHeight: 58,
  backgroundColor: mode === 'dark' ? '#171a20' : '#ffffff',
  borderRadius: 12,
  transition: 'border-color 160ms ease-out, box-shadow 160ms ease-out',
  '& fieldset': { borderColor: mode === 'dark' ? '#5f6673' : '#b9bcc4' },
  '&:hover fieldset': { borderColor: mode === 'dark' ? '#a3a9b5' : '#6b7280' },
  '&.Mui-focused': {
    boxShadow: `0 0 0 3px ${theme.palette.primary.main}24`,
  }
}));

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
    } catch (err: unknown) {
      setError(loginErrorMessage(err));
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
        <EvidencePanel aria-label="Nexora evidence-to-cash workflow" data-decorative-motion="true">
          <BrandHero />
          <Box
            sx={{
              '& .MuiTypography-root': { color: '#f8fafc !important' },
              '& img': { filter: 'brightness(0) invert(1)' },
              mb: { xs: 2, md: 7 },
            }}
          >
            <Branding fontSize={28} logoSize={38} showTagline={false} />
          </Box>

          <Typography
            component="p"
            sx={{
              maxWidth: 500,
              fontFamily: '"Cambay", "Source Sans 3", sans-serif',
              fontSize: { xs: 29, sm: 42, lg: 54 },
              fontWeight: 700,
              lineHeight: 1.02,
              letterSpacing: '-0.035em',
              color: '#f8fafc',
              mb: { xs: .75, md: 2 },
            }}
          >
            From source evidence to collected cash.
          </Typography>

          <Typography
            sx={{
              maxWidth: 500,
              color: '#cbc7bc',
              fontSize: { xs: 14, sm: 18 },
              lineHeight: 1.55,
              mb: { xs: 0, sm: 4.5 },
            }}
          >
            Ownership, approvals and status stay attached at every handoff.
          </Typography>

          <EvidenceSpine />

          <Box
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 1,
              mt: 'auto',
              pt: { xs: 2, sm: 4 },
              color: '#cbc7bc',
            }}
          >
            <IntegrityIcon aria-hidden="true" sx={{ color: '#a8a397', fontSize: 18 }} />
            <Typography sx={{ color: '#cbc7bc', fontSize: { xs: 11, sm: 12 }, lineHeight: 1.4 }}>
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
              sx={{
                width: 46,
                height: 46,
                border: '1px solid',
                borderColor: mode === 'dark' ? 'rgba(170, 176, 190, .36)' : 'rgba(82, 97, 116, .24)',
                borderRadius: '50%',
                background: mode === 'dark' ? 'rgba(18, 20, 24, .62)' : 'rgba(255, 255, 255, .68)',
                backdropFilter: 'blur(12px)',
                boxShadow: mode === 'dark' ? '0 12px 30px -24px #000' : '0 12px 30px -24px rgba(15, 18, 24, .7)',
              }}
            >
              {mode === 'dark' ? <SunIcon /> : <MoonIcon />}
            </IconButton>
          </Box>

          <AuthSurface mode={mode} className="nx-enter">
            <Typography
              component="h1"
              sx={{
                fontFamily: '"Cambay", "Source Sans 3", sans-serif',
                fontSize: { xs: 36, md: 46 },
                fontWeight: 700,
                lineHeight: 1.04,
                letterSpacing: '-0.035em',
                mb: 1.25,
              }}
            >
              Sign in
            </Typography>
            <Typography variant="body1" sx={{ color: mode === 'dark' ? '#cbc7bc' : '#5f6673', mb: { xs: 3, sm: 4 }, lineHeight: 1.55, maxWidth: 420, fontSize: { sm: 17 } }}>
              Welcome back. Use your work account to access your Nexora workspace.
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
                      sx={{ color: mode === 'dark' ? '#f3d27a' : '#c9931a', fontWeight: 600, fontSize: 16, textTransform: 'none' }}
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
                endIcon={!loading && !businessUnitOptions ? <ArrowIcon aria-hidden="true" /> : undefined}
                disabled={loading || (businessUnitOptions !== null && selectedBusinessUnitId === '')}
                aria-busy={loading}
                sx={{
                  minHeight: 58,
                  py: 1.5,
                  mt: 1,
                  fontSize: 16,
                  borderRadius: '12px',
                  '& .MuiButton-endIcon': { transition: 'transform 180ms ease-out' },
                  '&:hover .MuiButton-endIcon': { transform: 'translateX(3px)' },
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
                  sx={{ minHeight: 44, color: mode === 'dark' ? '#f3d27a' : '#c9931a', fontSize: 17, fontWeight: 600, textTransform: 'none' }}
                >
                  Platform administrator sign-in
                </Button>
              </Box>
            )}
          </AuthSurface>
        </FormSection>
      </LoginShell>
    </Container>
  );
};

export default LoginPage;
