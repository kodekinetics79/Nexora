import React, { useState } from 'react';
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
import Branding from '../../components/common/Branding';
import axiosInstance from '../../api/axiosInstance';
import { useNavigate } from 'react-router-dom';

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
  height: 100vh;
  width: 100vw;
  overflow: hidden;
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  background: ${(props: any) => props.theme.palette.background.default};
  font-family: 'Poppins', sans-serif;
  transition: background 0.5s ease;
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
  height: 850px;
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
    height: auto;
    min-height: 800px;
    flex-direction: column;
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
  color: #fff;
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

const LoginPage: React.FC = () => {
  const theme = useTheme();
  const { mode, setMode, primaryColor } = useAppTheme();
  const { setToken, setUserData, businessUnits, loadingBusinessUnits } = useAuth();
  const navigate = useNavigate();

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showPassword, setShowPassword] = useState(false);
  
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [businessUnitId, setBusinessUnitId] = useState<number | string>('');

  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError(null);
    try {
      const response = await axiosInstance.post("/api/Auth/Login", {
        email,
        password,
        businessUnitId,
      });
      const data = response.data;
      setToken(data.token);
      setUserData({
        id: data.id,
        email: data.email,
        userName: data.userName,
        roleName: data.roleName,
        roleId: data.roleId,
        businessUnitId: data.businessUnitId,
      });
      navigate('/dashboard');
    } catch (err: any) {
      setError(err.response?.data?.error || err.response?.data?.message || "Invalid credentials");
    } finally {
      setLoading(false);
    }
  };

  const nodeCenters = industrialFlow.map((_, i) => getNodeCenter(i));

  return (
    <Container theme={theme}>
      <DigitalGrid primary={primaryColor} />
      <ScannerLine primary={primaryColor} />

      <GlassCard mode={mode}>
        <VisualArea mode={mode}>
          <RadialWrapper>
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

          <Box sx={{ position: 'absolute', top: 50, left: 50, zIndex: 30 }}>
            <Branding fontSize={28} />
          </Box>
        </VisualArea>

        <FormSection>
          <Box sx={{ position: 'absolute', top: 40, right: 40, display: 'flex', gap: 2 }}>
            <IconButton onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')}>
              {mode === 'dark' ? <SunIcon /> : <MoonIcon />}
            </IconButton>
          </Box>

          <Box sx={{ maxWidth: 400, width: '100%', mx: 'auto' }}>
            <Typography variant="h3" sx={{ fontWeight: 900, mb: 1, letterSpacing: '-0.03em' }}>
              Welcome back
            </Typography>
            <Typography variant="body1" sx={{ color: 'text.secondary', mb: 6 }}>
              Enter your operational credentials to continue.
            </Typography>

            <form onSubmit={handleLogin}>
              <FormControl fullWidth sx={{ mb: 3 }}>
                <InputLabel id="bu-label">Select Business Unit</InputLabel>
                <StyledSelect
                  mode={mode}
                  labelId="bu-label"
                  label="Select Business Unit"
                  value={businessUnitId}
                  onChange={(e) => setBusinessUnitId(e.target.value as number)}
                  required
                  disabled={loadingBusinessUnits}
                >
                  {businessUnits.map((bu) => (
                    <MenuItem key={bu.id} value={bu.id}>
                      {bu.businessUnitName}
                    </MenuItem>
                  ))}
                  {loadingBusinessUnits && <MenuItem disabled><CircularProgress size={20} /></MenuItem>}
                </StyledSelect>
              </FormControl>

              <StyledTextField
                mode={mode}
                fullWidth
                label="Email Address"
                variant="outlined"
                sx={{ mb: 3 }}
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                required
                slotProps={{
                  input: {
                    startAdornment: (
                      <InputAdornment position="start">
                        <MailIcon sx={{ color: 'primary.main', opacity: 0.7 }} />
                      </InputAdornment>
                    ),
                  }
                }}
              />

              <StyledTextField
                mode={mode}
                fullWidth
                label="Password"
                type={showPassword ? 'text' : 'password'}
                variant="outlined"
                sx={{ mb: 4 }}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
                slotProps={{
                  input: {
                    startAdornment: (
                      <InputAdornment position="start">
                        <LockIcon sx={{ color: 'primary.main', opacity: 0.7 }} />
                      </InputAdornment>
                    ),
                    endAdornment: (
                      <InputAdornment position="end">
                        <IconButton onClick={() => setShowPassword(!showPassword)} edge="end">
                          {showPassword ? <VisibilityOff /> : <Visibility />}
                        </IconButton>
                      </InputAdornment>
                    ),
                  }
                }}
              />

              {error && <Alert severity="error" sx={{ mb: 3, borderRadius: 3 }}>{error}</Alert>}

              <Button
                fullWidth
                variant="contained"
                size="large"
                type="submit"
                disabled={loading}
                sx={{
                  py: 2,
                  fontSize: 16,
                  boxShadow: `0 12px 30px -10px ${primaryColor}66`,
                  '&:hover': { transform: 'translateY(-2px)' },
                  transition: 'all 0.3s ease',
                }}
              >
                {loading ? <CircularProgress size={24} color="inherit" /> : 'LOGIN'}
              </Button>
            </form>
          </Box>
        </FormSection>
      </GlassCard>
    </Container>
  );
};

export default LoginPage;
