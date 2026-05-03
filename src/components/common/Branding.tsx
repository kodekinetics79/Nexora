import React from 'react';
import { Box, Typography, useTheme } from '@mui/material';
import logo from '../../assets/img/logo.svg';

interface BrandingProps {
  fontSize?: number;
  logoSize?: number;
  showText?: boolean;
}

const Branding: React.FC<BrandingProps> = ({
  fontSize = 24,
  logoSize = 40,
  showText = true,
}) => {
  const theme = useTheme();

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, cursor: 'pointer' }}>
      <Box
        sx={{
          width: logoSize,
          height: logoSize,
          borderRadius: 2,
          backgroundColor: theme.palette.primary.main,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          flexShrink: 0,
          boxShadow: '0 4px 12px rgba(13, 71, 161, 0.2)',
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
              color: 'text.primary',
              fontFamily: '"Outfit", sans-serif',
              letterSpacing: '-1px',
              whiteSpace: 'nowrap',
            }}
          >
            FLUXA
          </Typography>
          <Typography
            sx={{
              fontSize: Math.max(9, Math.round(fontSize * 0.35)),
              fontWeight: 700,
              color: 'primary.main',
              letterSpacing: '0.1em',
              textTransform: 'uppercase',
              whiteSpace: 'nowrap',
              opacity: 0.8
            }}
          >
            Enterprise AI
          </Typography>
        </Box>
      )}
    </Box>
  );
};

export default Branding;
