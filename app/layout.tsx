import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import { headers } from "next/headers";
import "./globals.css";

const geistSans = Geist({ variable: "--font-geist-sans", subsets: ["latin"] });
const geistMono = Geist_Mono({ variable: "--font-geist-mono", subsets: ["latin"] });

export async function generateMetadata(): Promise<Metadata> {
  const incoming = await headers();
  const base = new URL(
    `${incoming.get("x-forwarded-proto") ?? "http"}://${incoming.get("host") ?? "localhost:3000"}`,
  );

  return {
    metadataBase: base,
    title: "Classroom Live",
    description: "교수님의 코드를 같은 교실에서 실시간으로 확인하세요.",
    openGraph: {
      title: "Classroom Live",
      description: "교수님의 코드를, 내 화면에서.",
      images: [{ url: new URL("/og.png", base).toString(), width: 1200, height: 630 }],
    },
    twitter: {
      card: "summary_large_image",
      title: "Classroom Live",
      description: "교수님의 코드를, 내 화면에서.",
      images: [new URL("/og.png", base).toString()],
    },
  };
}

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="ko">
      <body className={`${geistSans.variable} ${geistMono.variable}`}>{children}</body>
    </html>
  );
}
