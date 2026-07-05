import React from 'react';
import { Box, Typography, useTheme } from '@mui/material';
import logo from '../../assets/img/logo.svg';

interface BrandingProps {
  fontSize?: number;
  logoSize?: number;
  showText?: boolean;
  inverse?: boolean;
}

const Branding: React.FC<BrandingProps> = ({
  fontSize = 24,
  logoSize = 40,
  showText = true,
  inverse = false,
}) => {
  const theme = useTheme();

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, cursor: 'pointer' }}>
      <Box
        sx={{
          width: logoSize,
          height: logoSize,
          borderRadius: 2,
          backgroundColor: inverse ? 'rgba(255,255,255,0.08)' : theme.palette.primary.main,
          border: inverse ? '1px solid rgba(255,255,255,0.14)' : 'none',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          flexShrink: 0,
          boxShadow: inverse ? '0 10px 24px rgba(0,0,0,0.28)' : '0 4px 12px rgba(225, 29, 46, 0.22)',
        }}
      >
        <img 
          src={logo} 
          alt="Logo" 
          height={logoSize * 0.6} 
          style={{ filter: 'brightness(0) invert(1)' }} 
        />
      </Box>
      {showText && (
        <Box sx={{ display: 'flex', flexDirection: 'column', lineHeight: 1.1 }}>
          <Typography
            sx={{
              fontSize: fontSize,
              fontWeight: 900,
              color: inverse ? '#FFFFFF' : 'text.primary',
              fontFamily: '"Outfit", sans-serif',
              letterSpacing: '-1px',
              whiteSpace: 'nowrap',
            }}
          >
            NEXORA
          </Typography>
          <Typography
            sx={{
              fontSize: Math.max(9, Math.round(fontSize * 0.35)),
              fontWeight: 700,
              color: inverse ? 'rgba(255,255,255,0.78)' : 'primary.main',
              letterSpacing: '0.1em',
              textTransform: 'uppercase',
              whiteSpace: 'nowrap',
              opacity: 0.8
            }}
          >
            The Intelligence Platform
          </Typography>
        </Box>
      )}
    </Box>
  );
};

export default Branding;
