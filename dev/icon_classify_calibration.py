import numpy as np
from PIL import Image
U='./samples/'  # folder with the calibration screenshots
F={'basin':(U+'basin.png',3440/2576),'wall':(U+'wall.png',1.72),'city':(U+'city.png',1.72),
   'way4':(U+'wayfarer-4.png',1.72),'way5':(U+'wayfarer-5.png',1.72)}
S=[ # (img, kind, x, y, filled)
 ('basin','wp',1201,447,1),('basin','wp',1950,379,1),('basin','wp',1471,785,1),('basin','wp',268,505,1),('wall','wp',364,343,1),('wall','wp',1429,606,1),('city','wp',575,334,1),('way4','wp',527,330,1),
 ('way5','wp',857,237,0),
 ('basin','heart',1260,404,1),('basin','heart',1700,188,1),('basin','heart',985,786,1),('wall','heart',1088,391,1),('city','heart',585,284,1),
 ('way4','heart',1012,427,0),('way5','heart',1046,271,0),
 ('wall','poi',686,497,1),('wall','poi',927,606,1),('wall','poi',1237,600,1),('wall','poi',1779,418,1),('city','poi',874,302,1),('city','poi',250,489,1),('city','poi',1483,815,1),('basin','poi',1199,921,1),('way4','poi',909,323,1),
 ('way5','poi',1002,283,0),('way5','poi',1136,527,0),('way5','poi',788,717,0),
 ('basin','vista',1808,48,1),('wall','vista',1005,397,1),('wall','vista',1952,328,1),('city','vista',424,398,1),('city','vista',1563,246,1),
 ('basin','vista',1236,686,0),('way4','vista',872,428,0),
]
def masks(a):
    R,G,B=[a[...,i].astype(int) for i in range(3)]
    return {'wp':(B>215)&(G>185)&(R<170),
            'heart':(R>200)&(G>130)&(G<215)&(B<90)&(R-B>140),
            'poi':(R>180)&(G>80)&(G<170)&(B<90)&(R-G>50),
            'vista':(R>140)&(G<100)&(B<90)&(R-G>80)}
# per kind: patch half-size (px) and minimum fraction of patch that must be fill colour, searched over +-SEARCH offsets
P={'wp':4,'heart':5,'poi':2,'vista':4}
SEARCH=6
cache={}
out={}
for img,kind,x,y,filled in S:
    if img not in cache:
        f,s=F[img]; a=np.asarray(Image.open(f).convert('RGB')); cache[img]=(masks(a),s)
    M,s=cache[img]; m=M[kind]; cx,cy=int(x*s),int(y*s); h=P[kind]
    best=0
    for dy in range(-SEARCH,SEARCH+1):
        for dx in range(-SEARCH,SEARCH+1):
            patch=m[cy+dy-h:cy+dy+h+1, cx+dx-h:cx+dx+h+1]
            best=max(best,patch.mean())
    out.setdefault(kind,{0:[],1:[]})[filled].append(round(float(best),2))
for k,v in out.items(): print(k.ljust(6),'filled:',sorted(v[1]),'  hollow:',sorted(v[0]))
