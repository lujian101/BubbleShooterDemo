using System.Collections;
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using DG;
using DG.Tweening;

public class BubbleDragon : MonoBehaviour {

    const float TweenTime = 0.25f;
    const float Sqrt3Div2 = 0.86602540378443864676372317075294f;
    const float Sqrt3 = 1.7320508075688772935274463415059f;
    const float BoundFrameWidth = 0.1f;

    class BubbleCell {
        public int x;
        public int y;
        public GameObject go;
        public Material mat;
        public int color;
    }

    struct NPlane {
        public Vector3 point;
        public Vector3 normal;
    }

    struct iPoint : IEquatable<iPoint> {
        public int x;
        public int y;
        public iPoint( int _x, int _y ) { x = _x; y = _y; }
        public bool Equals( iPoint other ) {
            return x == other.x && y == other.y;
        }
        public override int GetHashCode() {
            return x.GetHashCode() ^ y.GetHashCode();
        }
    }

    class FlyingBubble {
        public bool stopped;
        public iPoint destResult;
        public Transform transform;
        public float radius;
        public Vector3 movedir;
        public float speed;
        public int color;
        public void Destroy() {
            if ( transform != null ) {
                UnityEngine.Object.DestroyObject( transform.gameObject );
            }
        }
    }

    class CellLine {
        public BubbleCell[] cells;
        public Transform root;
    }

    public TextAsset m_cellInfo = null;
    public GameObject m_bubbleGO = null;
    
    public int m_maxLine = 12;
    public int m_deadLine = 8;
    public int m_initLineCount = 4;
    public float m_initialSpeed = 10;
    public bool m_loop = true;
    
    // dockers
    Camera m_camera = null;
    Transform m_cellRoot = null;
    Transform m_aimer = null;
    Transform m_original = null;
    Transform m_bounds = null;
    
    // level metrics
    float m_boundsWidth = 0;
    float m_boundsHeight = 0;
    float m_lineHeight = 0;
    float m_bubbleRadius = 0;

    // level data
    int m_totalLine = 0;
    int m_firstLineOddLeading = 0;
    int[][] m_bubbleLevelData = null;
    int m_bubbleDataOffset = -1;
    bool m_levelUpdating = false;
    List<CellLine> m_buffer = null;
    NPlane[] m_planes = null;

    int m_shootBubbleState = 0;
    int m_shootBubbleColor = 0;
    FlyingBubble m_flyingBubble = null;
    
    // use to check bubble eliminating
    List<iPoint> m_openTable = new List<iPoint>();
    List<BubbleCell> m_eraseList = new List<BubbleCell>();
    HashSet<iPoint> m_closedTable = new HashSet<iPoint>();

    static Color[] ColorPalette = new Color[] {
        Color.red,
        Color.green,
        Color.blue,
        Color.yellow,
        Color.magenta,
        Color.grey
    };

    static Color GetColor( int colorIndex ) {
        return ColorPalette[ ( colorIndex - 1 ) % ColorPalette.Length ];
    }

    static void SetColor( Material mat, int colorIndex ) {
        if ( mat != null ) {
            mat.color = GetColor( colorIndex );
        }
    }

    static void FadeInBubble( Material mat, int colorIndex, Action callback = null, float alpha = 0.4f ) {
        if ( mat != null ) {
            var color = ColorPalette[ ( colorIndex - 1 ) % ColorPalette.Length ];
            color.a = 0.0f;
            mat.color = color;
            color.a = 1;
            var t = mat.DOColor( color, TweenTime );
            if ( callback != null ) {
                t.onComplete += () => callback();
            }
        }
    }

    static void FadeOutBubble( Material mat, Action callback ) {
        if ( mat != null ) {
            var color = mat.color;
            color.a = 0;
            var t = mat.DOColor( color, TweenTime );
            if ( callback != null ) {
                t.onComplete += () => callback();
            }
        }
    }

    static void FadeOutDestroy( BubbleCell b ) {
        b.color = 0;
        if ( b.go != null ) {
            var scale = b.go.transform.localScale;
            b.go.transform.DOScale( scale * 2, TweenTime );
        }
        FadeOutBubble(
            b.mat,
            () => {
                if ( b.go != null ) {
                    var parent = b.go.transform.parent;
                    b.go.transform.parent = null;
                    UnityEngine.Object.DestroyObject( b.go );
                    b.go = null;
                    if ( parent != null && parent.childCount == 0 ) {
                        UnityEngine.Object.DestroyObject( parent.gameObject );
                    }
                }
            }
        );
    }

    void Start() {
        m_buffer = new List<CellLine>();
        m_cellRoot = transform.Find( "Cell" );
        m_aimer = transform.Find( "Aimer" );
        m_original = transform;
        if ( m_cellRoot == null ) {
            m_cellRoot = new GameObject( "Cell" ).transform;
            m_cellRoot.parent = transform;
        }
        m_bounds = transform.Find( "Bounds" );
        m_bubbleGO = Resources.Load<GameObject>( "Bubble" );
        m_bounds.gameObject.SetActive( true );
        m_aimer.gameObject.SetActive( true );
        
        if ( m_cellInfo != null ) {
            var src = m_cellInfo.text;
            var lines = src.Split( '\n' );
            var slines = new List<String>();
            var width = int.MaxValue;
            for ( int i = 0; i < lines.Length; ++i ) {
                var l = lines[ i ].Trim();
                if ( !String.IsNullOrEmpty( l ) ) {
                    slines.Add( l );
                    if ( l.Length < width ) {
                        width = l.Length;
                    }
                }
            }
            m_bubbleDataOffset = slines.Count - 1;
            m_bubbleLevelData = new int[ slines.Count ][];
            for ( int j = 0; j < slines.Count; ++j ) {
                m_bubbleLevelData[ j ] = new int[ width ];
                var s = slines[ j ];
                for ( int i = 0; i < width; ++i ) {
                    var c = s[ i ];
                    if ( Char.IsNumber( c ) ) {
                        m_bubbleLevelData[ j ][ i ] = s[ i ] - '0';
                    }
                }
                if ( j == slines.Count - 1 ) {
                    m_firstLineOddLeading = m_bubbleLevelData[ j ][ 0 ] & 1;
                }
            } 
            var sphere = m_bubbleGO.GetComponent<SphereCollider>();
            m_bubbleRadius = sphere.radius;
            m_lineHeight = ( m_bubbleRadius * 2.0f ) * Sqrt3Div2;
            var frameWidth = width * m_bubbleRadius + m_bubbleRadius;
            var frameHeight = m_bubbleRadius * 2 + ( m_maxLine - 1 ) * m_lineHeight;
            if ( m_bounds != null ) {

                var frameScale = m_bounds.localScale;
                frameScale.z = m_bubbleRadius * 2;
                m_bounds.localScale = frameScale;

                m_planes = new NPlane[ 4 ];

                var l = m_bounds.Find( "Left" );
                var r = m_bounds.Find( "Right" );
                var t = m_bounds.Find( "Top" );
                var b = m_bounds.Find( "Bottom" );

                var lpos = l.localPosition;
                var lscale = l.localScale;
                lpos.x = -BoundFrameWidth * 0.5f;
                lpos.y = frameHeight * 0.5f;
                l.localPosition = lpos;
                lscale.y = frameHeight + BoundFrameWidth * 2;
                l.localScale = lscale;
                m_planes[ 0 ] = new NPlane {
                    point = new Vector3( lpos.x + BoundFrameWidth * 0.5f, lpos.y, lpos.z ),
                    normal = new Vector3( 1, 0, 0 )
                };

                var rpos = r.localPosition;
                var rscale = l.localScale;
                rpos.x = frameWidth + BoundFrameWidth * 0.5f;
                rpos.y = frameHeight * 0.5f;
                r.localPosition = rpos;
                rscale.y = frameHeight + BoundFrameWidth * 2;
                r.localScale = rscale;
                m_planes[ 1 ] = new NPlane {
                    point = new Vector3( rpos.x - BoundFrameWidth * 0.5f, rpos.y, rpos.z ),
                    normal = new Vector3( -1, 0, 0 )
                };

                var tpos = t.localPosition;
                var tscale = t.localScale;
                tpos.x = frameWidth * 0.5f;
                tpos.y = -BoundFrameWidth * 0.5f;
                t.localPosition = tpos;
                tscale.x = frameWidth;
                t.localScale = tscale;
                m_planes[ 2 ] = new NPlane {
                    point = new Vector3( tpos.x, tpos.y + BoundFrameWidth * 0.5f, tpos.z ),
                    normal = new Vector3( 0, 1, 0 )
                };

                var bpos = b.localPosition;
                var bscale = t.localScale;
                bpos.x = frameWidth * 0.5f;
                bpos.y = frameHeight + BoundFrameWidth * 0.5f;
                b.localPosition = bpos;
                bscale.x = frameWidth;
                b.localScale = bscale;
                m_planes[ 3 ] = new NPlane {
                    point = new Vector3( bpos.x, bpos.y - BoundFrameWidth * 0.5f, bpos.z ),
                    normal = new Vector3( 0, -1, 0 )
                };

                m_boundsWidth = frameWidth;
                m_boundsHeight = frameHeight;
                if ( m_aimer != null ) {
                    m_aimer.localPosition = new Vector3(
                        frameWidth * 0.5f,
                        frameHeight - m_bubbleRadius - BoundFrameWidth * 0.5f,
                        0
                    );
                }

                if ( m_camera == null ) {
                    m_camera = GetComponentInChildren<Camera>();
                    if ( m_camera != null ) {
                        var pos = m_camera.transform.localPosition;
                        pos.x = frameWidth * 0.5f;
                        pos.y = frameHeight * 0.5f;
                        m_camera.transform.localPosition = pos;
                    }
                }

                for ( int i = 0; i < m_initLineCount && i < m_maxLine; ++i ) {
                    NewLine( false );
                }
            }
        }
    }

    BubbleCell _StickToBuffer( ref FlyingBubble bubble ) {
        if ( !bubble.stopped ) {
            return null;
        }
        var cell = bubble.destResult;
        CellLine line;
        if ( cell.y >= m_buffer.Count ) {
            line = new CellLine {
                cells = new BubbleCell[ m_bubbleLevelData[ 0 ].Length ],
                root = null
            };
            m_buffer.Insert( 0, line );
        } else {
            line = m_buffer[ m_buffer.Count - 1 - cell.y ];
        }
        var slot = line.cells[ cell.x ] ?? new BubbleCell();
        if ( slot != null && slot.go != null ) {
            return null;
        }
        line.cells[ cell.x ] = slot;
        GameObject go = null;
        Material mat = null;
        var x = m_bubbleRadius * cell.x + m_bubbleRadius;
        var y = cell.y * m_lineHeight + m_bubbleRadius;
        slot.x = cell.x;
        slot.y = cell.y;
        go = UnityEngine.Object.Instantiate<GameObject>( m_bubbleGO );
        go.name = String.Format( "cell+{0}", cell.x );
        if ( line.root == null ) {
            var n = m_bubbleLevelData[ 0 ].Length;
            var lineRoot = new GameObject( String.Format( "line+{0}", m_buffer.Count ) ).transform;
            lineRoot.parent = m_cellRoot;
            lineRoot.localScale = Vector3.one;
            line.root = lineRoot;
        }
        go.transform.parent = line.root;
        go.transform.localPosition = bubble.transform.localPosition;
        go.transform.DOLocalMove( new Vector3( x, y, 0 ), TweenTime * 0.5f );
        
        mat = go.GetComponent<Renderer>().material;
        slot.go = go;
        slot.mat = mat;
        slot.color = bubble.color; 
        SetColor( mat, bubble.color );
        return line.cells[ cell.x ];
    }

    void _MoveToNextLine( BubbleCell[] line, bool withAnimation = true ) {
        for ( int i = 0; i < line.Length; ++i ) {
            var info = line[ i ];
            if ( info != null ) {
                info.y = info.y + 1;
                line[ i ] = info;
                var go = info.go;
                if ( go != null ) {
                    var new_y = info.y * m_lineHeight + m_bubbleRadius;
                    var new_x = m_bubbleRadius * info.x + m_bubbleRadius;
                    if ( go != null ) {
                        var new_pos = go.transform.localPosition;
                        new_pos.x = new_x;
                        new_pos.y = new_y;
                        if ( withAnimation ) {
                            go.transform.DOLocalMove( new_pos, TweenTime );
                        } else {
                            go.transform.localPosition = new_pos;
                        }
                    }
                }
            }
        }
    }

    bool _CheckDeadline() {
        int count = 0;
        if ( m_buffer.Count > m_deadLine ) {
            while ( m_buffer.Count > m_deadLine ) {
                var curLine = m_buffer[ 0 ];
                for ( int i = 0; i < curLine.cells.Length; ++i ) {
                    var b = curLine.cells[ i ];
                    if ( b != null && b.go != null ) {
                        FadeOutDestroy( b );
                    }
                }
                m_buffer.RemoveAt( 0 );
                ++count;
            }
        }
        return count != 0;
    }

    iPoint ToNearestCell( Vector3 pos ) {
        var cellWidth = m_bubbleRadius * 2;
        var cellHeight = m_bubbleRadius * Sqrt3;
        var y = pos.y;
        var cy = Mathf.FloorToInt( y / cellHeight );
        var padding = ( cy + m_totalLine + m_firstLineOddLeading ) & 1;
        var x = pos.x - padding * m_bubbleRadius;
        var cx = Mathf.FloorToInt( x / cellWidth ) * 2 + padding;
        return new iPoint { x = cx, y = cy };
    }

    Vector2 CellToWorld( iPoint cell ) {
        var new_y = cell.y * m_lineHeight + m_bubbleRadius;
        var new_x = m_bubbleRadius * cell.x + m_bubbleRadius;
        return new Vector2( new_x, new_y );
    }

    void NewLine( bool withAnimation = true ) {
        if ( m_bubbleDataOffset >= 0 ) {
            var curOffsetY = m_bubbleDataOffset--;
            var srcLine = m_bubbleLevelData[ curOffsetY ];
            if ( m_loop && m_bubbleDataOffset < 0 ) {
                m_bubbleDataOffset = m_bubbleLevelData.Length - 1;
            }
            for ( int i = 0; i < m_buffer.Count; ++i ) {
                _MoveToNextLine( m_buffer[ i ].cells, withAnimation );
            }
            var lineRoot = new GameObject( String.Format( "line-{0}", m_totalLine ) ).transform;
            lineRoot.parent = m_cellRoot;
            lineRoot.localScale = Vector3.one;
            var newLine = new CellLine();
            var newCells = new BubbleCell[ srcLine.Length ];
            for ( int i = 0; i < srcLine.Length; ++i ) {
                var dx = m_bubbleRadius * i + m_bubbleRadius;
                var tag = m_bubbleLevelData[ curOffsetY ][ i ];
                GameObject go = null;
                Material mat = null;
                if ( tag != 0 ) {
                    go = UnityEngine.Object.Instantiate<GameObject>( m_bubbleGO );
                    go.name = String.Format( "cell-{0}", i );
                    go.transform.parent = lineRoot;
                    var dstPos = new Vector3( dx, m_bubbleRadius, 0 );
                    if ( withAnimation ) {
                        var srcPos = new Vector3( dx, -m_bubbleRadius, 0 );
                        go.transform.localPosition = srcPos;
                        var t = go.transform.DOLocalMove( dstPos, TweenTime );
                        m_levelUpdating = true;
                        t.onComplete += () => {
                            m_levelUpdating = false;
                        };
                    } else {
                        go.transform.localPosition = dstPos;
                    }
                    mat = go.GetComponent<Renderer>().material;
                    FadeInBubble( mat, tag );
                }
                var bi = new BubbleCell {
                    x = i,
                    y = 0,
                    go = go,
                    mat = mat,
                    color = tag,
                };
                newCells[ i ] = bi;
            }
            newLine.cells = newCells;
            newLine.root = lineRoot;
            m_buffer.Add( newLine );
            ++m_totalLine;
        }
    }

    static float CollisionTest_Sphere_Plane( Vector3 ori, Vector3 dir, float step, float dt, float radius, NPlane plane ) {
        var offset = dir * step;
        var s = ori - plane.point;
        var dis = Vector3.Dot( s, plane.normal );
        if ( dis <= radius ) {
            // already collided: ignore...
            return -1;
        }
        var dst = ori + offset;
        var s2 = dst - plane.point;
        var dis2 = Vector3.Dot( s2, plane.normal );
        if ( dis2 < radius ) {
            var vel = offset / dt;
            var speed = Vector3.Dot( offset / dt, -plane.normal );
            var t = ( dis - radius ) / speed;
            if ( t >= 0 ) {
                return t;
            }
        }
        return -1;
    }

    static float CollisionTest_Sphere_Sphere( Vector3 c0, Vector3 dir, float speed, float r0, Vector3 c1, float r1 ) {
        var r = r0 + r1;
        var r2 = r * r;
        var l = c1 - c0; 
        // center distance square
        var disSq = l.sqrMagnitude;
        if ( disSq <= r2 ) {
            // already collided: ignore...
            return -1;
        }
        var s = Vector3.Dot( l, dir );
        if ( s < 0 ) {
            return -1;
        }
        var p = c0 + s * dir;
        var n = c1 - p;
        var nLenSq = n.sqrMagnitude;
        if ( nLenSq > r2 ) {
            return -1;
        }
        var nlen = Mathf.Sqrt( nLenSq );
        n = n / nlen; // normlize
        var h = Mathf.Sqrt( r2 - nLenSq );
        var hit = p - dir * h;
        return ( hit - c0 ).magnitude / speed;
    }

    static Vector3 Reflect( Vector3 i, Vector3 n ) {
        return n * Vector3.Dot( -i, n ) * 2 + i;
    }

    void HitTest( FlyingBubble bubble ) {
        var time = Time.deltaTime;
        var curPos = bubble.transform.localPosition;
        var hitObjType = 0;
        for ( ; time > 0 && bubble.speed > 0; ) {
            NPlane plane;
            var hitTime = time;
            var movedir = bubble.movedir;
            for ( int i = 0; i < m_planes.Length; ++i ) {
                var step = bubble.speed * time;
                var t = CollisionTest_Sphere_Plane( curPos, bubble.movedir, step, time, bubble.radius, m_planes[ i ] );
                if ( t >= 0 && t < hitTime ) {
                    hitTime = t;
                    plane = m_planes[ i ];
                    movedir = Reflect( bubble.movedir, m_planes[ i ].normal );
                    hitObjType = 1;
                }
            }
            for ( int j = 0; j < m_buffer.Count; ++j ) {
                var line = m_buffer[ j ];
                for ( int i = 0; i < line.cells.Length; ++i ) {
                    var bi = line.cells[ i ];
                    if ( bi != null && bi.go != null ) {
                        var c1 = bi.go.transform.localPosition;
                        var c0 = curPos;
                        var t = CollisionTest_Sphere_Sphere( c0, bubble.movedir, bubble.speed, bubble.radius, c1, bubble.radius );
                        if ( t >= 0 && t < hitTime ) {
                            hitTime = t;
                            movedir = Reflect( bubble.movedir, ( c0 - c1 ).normalized );
                            hitObjType = 2;
                        }
                    }
                }
            }
            var offset = bubble.movedir * bubble.speed * hitTime;
            curPos = curPos + offset;
            time -= hitTime;
            bubble.movedir = movedir;
            if ( time > 0 ) {
                if ( hitObjType == 2 ) {
                    bubble.speed = 0;
                    bubble.stopped = true;
                    bubble.destResult = ToNearestCell( curPos );
                }
            }
        }
        bubble.transform.localPosition = curPos;
    }

    BubbleCell GetBubble( int x, int y ) {
        if ( y < 0 || y >= m_buffer.Count || x < 0 ) {
            return null;
        }
        var line = m_buffer[ m_buffer.Count - 1 - y ];
        if ( line == null || line.cells == null || x >= line.cells.Length || line.cells[ x ] == null ) {
            return null;
        }
        return line.cells[ x ];
    }

    int GetBubbleColor( int x, int y ) {
        if ( y < 0 || y >= m_buffer.Count || x < 0 ) {
            return -1;
        }
        var line = m_buffer[ m_buffer.Count - 1 - y ];
        if ( line == null || line.cells == null || x >= line.cells.Length || line.cells[ x ] == null ) {
            return -1;
        }
        return line.cells[ x ].color;
    }

    void CheckEliminate( iPoint pt, int color, List<BubbleCell> eraseList, int threshold ) {
        m_openTable.Clear();
        m_closedTable.Clear();
        var count = 1;
        try {
            m_openTable.Add( new iPoint( pt.x, pt.y ) );
            while ( m_openTable.Count > 0 ) {
                int last = m_openTable.Count - 1;
                iPoint cur = m_openTable[ last ];
                m_openTable.RemoveAt( last );
                m_closedTable.Add( cur );
                var a = new iPoint( cur.x + 2, cur.y );
                if ( GetBubbleColor( a.x, a.y ) == color ) {
                    if ( !m_closedTable.Contains( a ) ) {
                        m_openTable.Add( a );
                        ++count;
                    }
                }
                var b = new iPoint( cur.x - 2, cur.y );
                if ( GetBubbleColor( b.x, b.y ) == color ) {
                    if ( !m_closedTable.Contains( b ) ) {
                        m_openTable.Add( b );
                        ++count;
                    }
                }
                var c = new iPoint( cur.x - 1, cur.y - 1 );
                if ( GetBubbleColor( c.x, c.y ) == color ) {
                    if ( !m_closedTable.Contains( c ) ) {
                        m_openTable.Add( c );
                        ++count;
                    }
                }
                var d = new iPoint( cur.x + 1, cur.y + 1 );
                if ( GetBubbleColor( d.x, d.y ) == color ) {
                    if ( !m_closedTable.Contains( d ) ) {
                        m_openTable.Add( d );
                        ++count;
                    }
                }
                var e = new iPoint( cur.x - 1, cur.y + 1 );
                if ( GetBubbleColor( e.x, e.y ) == color ) {
                    if ( !m_closedTable.Contains( e ) ) {
                        m_openTable.Add( e );
                        ++count;
                    }
                }
                var f = new iPoint( cur.x + 1, cur.y - 1 );
                if ( GetBubbleColor( f.x, f.y ) == color ) {
                    if ( !m_closedTable.Contains( f ) ) {
                        m_openTable.Add( f );
                        ++count;
                    }
                }
            }
        } finally {
            if ( eraseList != null ) {
                if ( m_closedTable.Count > threshold ) {
                    foreach ( iPoint p in m_closedTable ) {
                        var b = GetBubble( p.x, p.y );
                        if ( b != null && b.go != null ) {
                            m_eraseList.Add( b );
                        }
                    }
                }
            }
            m_openTable.Clear();
            m_closedTable.Clear();
        }
    }

    void DoEliminateBubbles( List<BubbleCell> eraseList ) {
        for ( int i = 0; i < eraseList.Count; ++i ) {
            var b = eraseList[ i ];
            if ( b.go != null ) {
                FadeOutDestroy( b );
            }
        }
    }

    void BubbleMove( ref FlyingBubble bubble ) {
        if ( !bubble.stopped ) {
            HitTest( bubble );
            if ( bubble.stopped ) {
                var slot = _StickToBuffer( ref bubble );
                if ( slot != null ) {
                    if ( !_CheckDeadline() ) {
                        try {
                            CheckEliminate( new iPoint( slot.x, slot.y ), slot.color, m_eraseList, 3 );
                            DoEliminateBubbles( m_eraseList );
                        } finally {
                            m_eraseList.Clear();
                        }
                    } else {
                    }
                }
                bubble.Destroy();
                bubble = null;
            }
        }
    }

    void Update() {
        if ( Input.GetKeyDown( KeyCode.Space ) && m_flyingBubble == null && m_levelUpdating == false ) {
            NewLine();
            _CheckDeadline();
        }
        if ( Input.GetMouseButton( 0 ) ) {
            var pt = Input.mousePosition;
            if ( m_camera != null ) {
                var o = m_camera.WorldToScreenPoint( m_aimer.position );
                o.y = Mathf.Clamp( o.y, 0, m_boundsHeight );
                o.z = 0;
                pt.z = 0;
                var dir = pt - o;
                if ( dir.sqrMagnitude > Vector3.kEpsilon ) {
                    var rangle = Mathf.Atan2( dir.y, dir.x );
                    var angle = Mathf.Rad2Deg * rangle;
                    angle = Mathf.Clamp( angle, 30, 150 );
                    angle -= 90;
                    m_aimer.rotation = Quaternion.Euler( 0, 0, angle );
                }
            }
        }
        if ( m_shootBubbleState == 0 ) {
            m_shootBubbleState = 1;
            m_shootBubbleColor = UnityEngine.Random.Range( 0, ColorPalette.Length ) + 1;
            var bubble = UnityEngine.Object.Instantiate<GameObject>( m_bubbleGO );
            bubble.name = "[bubble]";
            bubble.transform.parent = m_aimer;
            bubble.transform.localPosition = Vector3.zero;
            FadeInBubble(
                bubble.GetComponent<Renderer>().material,
                m_shootBubbleColor,
                () => {
                    m_shootBubbleState = 2;
                }
            );
        }
        if ( m_shootBubbleState == 2 && Input.GetMouseButtonUp( 0 ) && m_flyingBubble == null ) {
            m_shootBubbleState = 0;
            var b = m_aimer.Find( "[bubble]" );
            if ( b != null ) {
                b.transform.parent = m_original;
                m_flyingBubble = new FlyingBubble();
                m_flyingBubble.radius = m_bubbleRadius;
                m_flyingBubble.transform = b.transform;
                m_flyingBubble.movedir = m_aimer.parent.TransformVector( m_aimer.up );
                m_flyingBubble.speed = m_initialSpeed;
                m_flyingBubble.color = m_shootBubbleColor;
            }
        }
        if ( m_flyingBubble != null ) {
            BubbleMove( ref m_flyingBubble );
        }
    }
}
