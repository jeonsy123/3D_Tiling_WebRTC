const fs = require('fs');
const https = require('https');
const express = require('express');
const cors = require('cors');
const path = require('path');

const app = express();
const PORT = 8081;

app.use(cors());

// 캐싱 헤더 설정용 함수
const setCacheHeaders = (res) => {
  res.setHeader('Cache-Control', 'public, max-age=31536000'); // 1년 캐싱
};

// 정적 파일: Plytiles (.ply 파일들)
app.use('/tiles', express.static('D:/test project/Server/Assets/Plytiles', {
  maxAge: '1y',
  setHeaders: setCacheHeaders
}));

// 정적 파일: StreamingAssets
app.use(express.static('D:/test project/Ply_Player(blb)/WebGLBuild/StreamingAssets', {
  maxAge: '1y',
  setHeaders: setCacheHeaders
}));

// 정적 파일: WebGLBuild (loader.js, data files 등)
app.use(express.static('D:/test project/Ply_Player(blb)/WebGLBuild', {
  maxAge: '1y',
  setHeaders: setCacheHeaders
}));

// 인증서 설정
const options = {
  key: fs.readFileSync('./cert/key.pem'),
  cert: fs.readFileSync('./cert/cert.pem')
};

// HTTPS 서버 시작
https.createServer(options, app).listen(PORT, () => {
  console.log(`✅ HTTPS 서버 실행 중: https://192.168.0.22:${PORT}`);
});
