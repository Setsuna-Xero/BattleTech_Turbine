using BattleTech;
using BattleTech.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Sheepy.BattleTechMod.Turbine {
   using System.Threading;
   using static Mod;
   using static System.Reflection.BindingFlags;

   public class DataProcess : BattleModModule {

      public override void ModStarts () {
         Patch( typeof( HBS.Util.JSONSerializationUtility ), "StripHBSCommentsFromJSON", NonPublic | Static, "Override_StripComments", null );
         Patch( typeof( DataManager ), "GetDataHash", Static, "MultiThreadDataHash", null );
      }

      // ============ Json Process ============

      public static bool Override_StripComments ( ref String __result, String json ) { try {
         __result = StripComments( json );
         return false;
      }                 catch ( Exception ex ) { return Error( ex ); } }

      public static String StripComments ( String json ) {
         int pos = 0;
         StringBuilder buf = new StringBuilder( json.Length );
         do {
Loop:
            for ( int i = pos, len = json.Length - 2 ; i < len ; i++ ) {
               char a = json[ i ];
               if ( a == '/' ) { // Detect //* to */
                  char b = Peek( json, i+1 );
                  if ( b == '/' ) {
                     if ( Peek( json, i+2 ) == '*' ) { // //* to */
                        if ( SkipWS( buf, json, ref pos, i, i+3, "*/" ) ) goto Loop;
                     } /*else {                          // Single line comment // to \n, conflict with url string and requires string state tracking
                        if ( Skip( buf, json, ref pos, i, i+2, "\n" ) ) {
                           buf.Append( '\n' );
                           goto Loop;
                        }
                     }*/
                  } else if ( b == '*' ) { // /* to */
                     if ( SkipWS( buf, json, ref pos, i, i+2, "*/" ) ) goto Loop;
                  }
               } else if ( a == '<' && Match( json, i+1, "!--" ) ) { // <!-- to -->
                  if ( SkipWS( buf, json, ref pos, i, i+4, "-->" ) ) goto Loop;
               }
            }
            // Nothing found, copy everything and break
            buf.Append( json.Substring( pos ) );
            break;
         } while ( true );
         return buf.ToString();
      }

      private static bool Match ( String json, int pos, String txt ) {
         if ( json.Length <= pos + txt.Length ) return false;
         String sub = json.Substring( pos, txt.Length );
         return sub == txt;
      }
      private static bool SkipWS ( StringBuilder buf, String json, ref int pos, int skipStart, int headEnd, String until ) {
         if ( ! Skip( buf, json, ref pos, skipStart, headEnd, until ) ) return false;
         int len = json.Length;
         while ( pos < len ) {
            switch ( json[ pos ] ) {
               case ' ': case '\t': case '\r': case '\n':
                  pos++;
                  break;
               default:
                  return true;
            }
         }
         return true;
      }
      private static bool Skip ( StringBuilder buf, String json, ref int pos, int skipStart, int headEnd, String until ) {
         if ( json.Length <= headEnd ) return false;
         int tailStart = json.IndexOf( until, headEnd );
         if ( tailStart < 0 ) return false;
         if ( skipStart > 0 )
            buf.Append( json.Substring( pos, skipStart - pos ) );
         pos = tailStart + until.Length;
         return true;
      }
      private static char Peek ( String json, int pos ) {
         if ( json.Length <= pos ) return '\u0000';
         return json[ pos ];
      }

      // ============ Data Hash ============

      private static byte[] SecretKey;

      public static bool MultiThreadDataHash ( ref string __result, params BattleTechResourceType[] typesToHash ) { try {
         if ( SecretKey == null ) {
            SecretKey = (byte[]) typeof( DataManager ).GetField( "secret_key", NonPublic | Static ).GetValue( null );
            if ( SecretKey == null ) throw new NullReferenceException( "DataManager.secret_key is null" );
         }
         BattleTechResourceLocator battleTechResourceLocator = new BattleTechResourceLocator();
         int counter = 0;
         Dictionary<int,VersionManifestEntry> manifestList = new Dictionary<int,VersionManifestEntry>( 4000 ); // Vanilla has 900+. Mods may adds a lot more.
         foreach ( BattleTechResourceType type in typesToHash )
            foreach ( VersionManifestEntry versionManifestEntry in battleTechResourceLocator.AllEntriesOfResource( type ) )
               manifestList.Add( counter++, versionManifestEntry );

         Dictionary<int,byte[]> hashSet = new Dictionary<int,byte[]>();
         int threadCount = 1, doneThread = 0;
         for ( int i = 0 ; i < threadCount ; i++ ) {
            new Thread( () => { 
               HMACSHA256 hasher = new HMACSHA256( SecretKey );
               for ( int j = 0 ; j < counter ; j++ ) {
                  VersionManifestEntry versionManifestEntry = manifestList[ j ];
                  if ( ! versionManifestEntry.IsAssetBundled && ! versionManifestEntry.IsResourcesAsset && File.Exists( versionManifestEntry.FilePath ) ) {
                     try {
                        using ( FileStream fileStream = new FileStream( versionManifestEntry.FilePath, FileMode.Open, FileAccess.Read ) ) {
                           hashSet.Add( j, hasher.ComputeHash( fileStream ) );
                        }
                     } catch ( Exception ex ) {
                        Error( "Could not calculate hash on {0}: {1}", versionManifestEntry.FilePath, ex );
                     }
                  }
               }
               lock( hashSet ) { 
                  ++doneThread;
                  if ( doneThread == threadCount )
                     Monitor.Pulse( hashSet );
               }
            } ).Start();
         }

         HMACSHA256 hmacsha = new HMACSHA256( SecretKey );
         List<byte[]> hashList = new List<byte[]>();
         lock( hashSet ) { 
            if ( doneThread != threadCount )
               Monitor.Wait( hashSet );
         }
         for ( int i = 0 ; i < counter ; i++ )
            hashList.Add( hashSet[i] );
         __result = Convert.ToBase64String( hmacsha.ComputeHash( hashList.SelectMany( ( byte[] x ) => x ).ToArray<byte>() ) );
         Verbo( "Hash = {0}", __result );
         return false;
      }                 catch ( Exception ex ) { return Error( ex ); } }
   }
}