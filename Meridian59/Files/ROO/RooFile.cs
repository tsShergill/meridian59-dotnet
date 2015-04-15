﻿/*
 Copyright (c) 2012-2013 Clint Banzhaf
 This file is part of "Meridian59 .NET".

 "Meridian59 .NET" is free software: 
 You can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, 
 either version 3 of the License, or (at your option) any later version.

 "Meridian59 .NET" is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
 without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 See the GNU General Public License for more details.

 You should have received a copy of the GNU General Public License along with "Meridian59 .NET".
 If not, see http://www.gnu.org/licenses/.
*/

using System;
using System.IO;
using System.Collections.Generic;
using Meridian59.Common.Interfaces;
using Meridian59.Common.Constants;
using Meridian59.Files.BGF;
using Meridian59.Common;
using Meridian59.Common.Enums;
using Meridian59.Protocol.GameMessages;
using Meridian59.Protocol.Enums;
using Meridian59.Data.Models;

// Switch FP precision based on architecture
#if X64
using Real = System.Double;
#else 
using Real = System.Single;
#endif

namespace Meridian59.Files.ROO
{
    /// <summary>
    /// Meridian 59 map file. Containing BSP-Tree, Texture information and more.
    /// </summary>
    [Serializable]
    public class RooFile : IGameFile, IByteSerializableFast, ITickable
    {
        #region Constants
        public const uint SIGNATURE             = 0xB14F4F52;   // first expected bytes in file
        public const uint VERSION               = 13;           // current
        public const uint VERSIONMONSTERGRID    = 12;           // first one with monster grid
        public const uint VERSIONHIGHRESGRID    = 13;           // first one with highres grid
        public const uint VERSIONSPEED          = 10;           // first one that has additional "speed" values
        public const uint MINVERSION            = 9;            // absolute minimum
        public const uint ENCRYPTIONFLAG        = 0xFFFFFFFF;
        public const byte ENCRYPTIONINFOLENGTH  = 12;
        public const int DEFAULTCLIENTOFFSET    = 20;
        public const int DEFAULTDEPTH0          = 0;
        public const int DEFAULTDEPTH1          = GeometryConstants.FINENESS / 5;
        public const int DEFAULTDEPTH2          = 2 * GeometryConstants.FINENESS / 5;
        public const int DEFAULTDEPTH3          = 3 * GeometryConstants.FINENESS / 5;
        public const string ERRORCRUSHPLATFORM  = "Crusher is only supported in x86 builds on Windows.";      
        public static byte[] PASSWORDV12 = new byte[] { 0x6F, 0xCA, 0x54, 0xB7, 0xEC, 0x64, 0xB7, 0x00 };
        public static byte[] PASSWORDV10 = new byte[] { 0x15, 0x20, 0x53, 0x01, 0xFC, 0xAA, 0x64, 0x00 };
        public static readonly int[] SectorDepths = new int[] { DEFAULTDEPTH0, DEFAULTDEPTH1, DEFAULTDEPTH2, DEFAULTDEPTH3 };     
        #endregion

        #region Events
        /// <summary>
        /// Raised when a texture on a wallside changed.
        /// </summary>
        public event WallTextureChangedEventHandler WallTextureChanged;

        /// <summary>
        /// Raised when a texture on a floor or ceiling changed.
        /// </summary>
        public event SectorTextureChangedEventHandler SectorTextureChanged;

        /// <summary>
        /// Raised when Update() is called and a sector moved a bit.
        /// </summary>
        public event SectorMovedEventHandler SectorMoved;
        #endregion

        #region IByteSerializable
        public int ByteLength
        {
            get 
            {
                int len = TypeSizes.INT + TypeSizes.INT + TypeSizes.INT + TypeSizes.INT + TypeSizes.INT;

                if (EncryptionEnabled)
                    len += TypeSizes.INT + TypeSizes.INT + TypeSizes.INT;

                // basicinfo (roomsize, offsets.. 8*4=32 bytes)
                len += TypeSizes.INT + TypeSizes.INT + TypeSizes.INT + TypeSizes.INT + 
                    TypeSizes.INT + TypeSizes.INT + TypeSizes.INT + TypeSizes.INT;

                // section 1
                len += TypeSizes.SHORT;
                foreach (RooBSPItem bspItem in BSPTreeLeaves)
                    len += bspItem.ByteLength;
                
                foreach (RooBSPItem bspItem in BSPTreeNodes)
                    len += bspItem.ByteLength;

                // section 2
                len += TypeSizes.SHORT;
                foreach (RooWall lineDef in Walls)
                    len += lineDef.ByteLength;

                // section 3
                len += TypeSizes.SHORT;
                foreach (RooWallEditor section3Item in WallsEditor)
                    len += section3Item.ByteLength;

                // section 4
                len += TypeSizes.SHORT;
                foreach (RooSideDef sideDef in SideDefs)
                    len += sideDef.ByteLength;

                // section 5
                len += TypeSizes.SHORT;
                foreach (RooSector sectorDef in Sectors)
                    len += sectorDef.ByteLength;

                // section 6
                len += TypeSizes.SHORT;
                foreach (RooThing section6Item in Things)
                    len += section6Item.ByteLength;

                // roomid
                len += TypeSizes.INT;

                len += Grids.ByteLength;

                return len;
            }
        }

        public int WriteTo(byte[] Buffer, int StartIndex = 0)
        {
            int cursor = StartIndex;
            int encryptionStartOffset = -1;

            // Write header
            Array.Copy(BitConverter.GetBytes(SIGNATURE), 0, Buffer, cursor, TypeSizes.INT);  // Signature    (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(RooVersion), 0, Buffer, cursor, TypeSizes.INT);        // MapType      (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(Challenge), 0, Buffer, cursor, TypeSizes.INT);         // Challenge    (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(OffsetClient), 0, Buffer, cursor, TypeSizes.INT);      // OffsetClient (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(OffsetServer), 0, Buffer, cursor, TypeSizes.INT);      // OffsetServer (4 bytes)
            cursor += TypeSizes.INT;

            //
            // Client part (was encrypted once)
            // 

            if (EncryptionEnabled)
            {
                Array.Copy(BitConverter.GetBytes(ENCRYPTIONFLAG), 0, Buffer, cursor, TypeSizes.INT);            // EncryptionFlag (4 bytes)
                cursor += TypeSizes.INT;

                Array.Copy(BitConverter.GetBytes(EncryptedStreamLength), 0, Buffer, cursor, TypeSizes.INT);     // EncryptedStreamLength (4 bytes)
                cursor += TypeSizes.INT;

                // This is the ExpectedResponse, but we don't know it yet, see below...             // ExpectedResponse (4 bytes)
                cursor += TypeSizes.INT;

                // save the cursorposition as startindex for encryption later
                encryptionStartOffset = cursor;
            }

            // BasicInfo            
            Array.Copy(BitConverter.GetBytes(RoomSizeX), 0, Buffer, cursor, TypeSizes.INT);            // RoomSizeX        (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(RoomSizeY), 0, Buffer, cursor, TypeSizes.INT);            // RoomSizeY        (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(OffsetBSPTree), 0, Buffer, cursor, TypeSizes.INT);        // OffsetBSPTree    (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(OffsetWalls), 0, Buffer, cursor, TypeSizes.INT);       // OffsetLineDefs   (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(OffsetWallsEditor), 0, Buffer, cursor, TypeSizes.INT);       // OffsetSection3   (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(OffsetSideDefs), 0, Buffer, cursor, TypeSizes.INT);       // OffsetSideDefs   (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(OffsetSectors), 0, Buffer, cursor, TypeSizes.INT);     // OffsetSectorDefs (4 bytes)
            cursor += TypeSizes.INT;

            Array.Copy(BitConverter.GetBytes(OffsetThings), 0, Buffer, cursor, TypeSizes.INT);       // OffsetSection6   (4 bytes)
            cursor += TypeSizes.INT;

            // Section 1: BSP-Tree
            ushort len = Convert.ToUInt16(BSPTree.Count);
            Array.Copy(BitConverter.GetBytes(len), 0, Buffer, cursor, TypeSizes.SHORT);                  // Section1ItemsLEN (2 bytes)
            cursor += TypeSizes.SHORT;
            
            foreach (RooBSPItem bspItem in BSPTree)
                cursor += bspItem.WriteTo(Buffer, cursor);
          
            // Section 2: Walls
            len = Convert.ToUInt16(Walls.Count);
            Array.Copy(BitConverter.GetBytes(len), 0, Buffer, cursor, TypeSizes.SHORT);                  // Section2ItemsLEN (2 bytes)
            cursor += TypeSizes.SHORT;

            foreach (RooWall wall in Walls)
                cursor += wall.WriteTo(Buffer, cursor);

            // Section 3: Unknown
            len = Convert.ToUInt16(WallsEditor.Count);
            Array.Copy(BitConverter.GetBytes(len), 0, Buffer, cursor, TypeSizes.SHORT);                  // Section3ItemsLEN (2 bytes)
            cursor += TypeSizes.SHORT;

            foreach (RooWallEditor section3Item in WallsEditor)
                cursor += section3Item.WriteTo(Buffer, cursor);

            // Section 4: SideDefs
            len = Convert.ToUInt16(SideDefs.Count);
            Array.Copy(BitConverter.GetBytes(len), 0, Buffer, cursor, TypeSizes.SHORT);                  // SideDefsLEN (2 bytes)
            cursor += TypeSizes.SHORT;

            foreach (RooSideDef sideDef in SideDefs)
                cursor += sideDef.WriteTo(Buffer, cursor);

            // Section 5: Sectors
            len = Convert.ToUInt16(Sectors.Count);
            Array.Copy(BitConverter.GetBytes(len), 0, Buffer, cursor, TypeSizes.SHORT);                  // SectorDefsLEN (2 bytes)
            cursor += TypeSizes.SHORT;

            foreach (RooSector sectorDef in Sectors)
                cursor += sectorDef.WriteTo(Buffer, cursor);

            // Section 6: Unknown
            len = Convert.ToUInt16(Things.Count);
            Array.Copy(BitConverter.GetBytes(len), 0, Buffer, cursor, TypeSizes.SHORT);                  // Section6ItemsLEN (2 bytes)
            cursor += TypeSizes.SHORT;

            foreach (RooThing section6Item in Things)
                cursor += section6Item.WriteTo(Buffer, cursor);

            Array.Copy(BitConverter.GetBytes(RoomID), 0, Buffer, cursor, TypeSizes.INT);               // RoomID (4 bytes)
            cursor += TypeSizes.INT;

            // Do the encryption
            if (EncryptionEnabled)
            {
#if WINCLR && X86
                ExpectedResponse = Crush32.Encrypt(Buffer, encryptionStartOffset, cursor - encryptionStartOffset, Challenge, GetPassword());

                // Write the expected response ( was updated in Encrypt() )
                Array.Copy(BitConverter.GetBytes(ExpectedResponse), 0, Buffer, StartIndex + DEFAULTCLIENTOFFSET + 8, TypeSizes.INT); 
#else
                throw new Exception(ERRORCRUSHPLATFORM);
#endif
            }

            //
            // server part (was never encrypted)
            // 

            cursor += Grids.WriteTo(Buffer, cursor);

            return cursor - StartIndex;
        }

        public unsafe void WriteTo(ref byte* Buffer)
        {
            int encryptionStartOffset = -1;

            // Write header
            *((uint*)Buffer) = SIGNATURE;
            Buffer += TypeSizes.INT;

            *((uint*)Buffer) = RooVersion;
            Buffer += TypeSizes.INT;

            *((uint*)Buffer) = Challenge;
            Buffer += TypeSizes.INT;

            *((uint*)Buffer) = OffsetClient;
            Buffer += TypeSizes.INT;

            *((int*)Buffer) = OffsetServer;
            Buffer += TypeSizes.INT;

            //
            // Client part (was encrypted once)
            // 

            if (EncryptionEnabled)
            {
                *((uint*)Buffer) = ENCRYPTIONFLAG;
                Buffer += TypeSizes.INT;

                *((int*)Buffer) = EncryptedStreamLength;
                Buffer += TypeSizes.INT;

                // This is the ExpectedResponse, but we don't know it yet, see below...
                Buffer += TypeSizes.INT;

                // save the cursorposition as startindex for encryption later
                encryptionStartOffset = (int)Buffer;
            }

            // BasicInfo            
            *((uint*)Buffer) = RoomSizeX;
            Buffer += TypeSizes.INT;

            *((uint*)Buffer) = RoomSizeY;
            Buffer += TypeSizes.INT;

            *((int*)Buffer) = OffsetBSPTree;
            Buffer += TypeSizes.INT;

            *((int*)Buffer) = OffsetWalls;
            Buffer += TypeSizes.INT;

            *((int*)Buffer) = OffsetWallsEditor;
            Buffer += TypeSizes.INT;

            *((int*)Buffer) = OffsetSideDefs;
            Buffer += TypeSizes.INT;

            *((int*)Buffer) = OffsetSectors;
            Buffer += TypeSizes.INT;

            *((int*)Buffer) = OffsetThings;
            Buffer += TypeSizes.INT;

            // Section 1: BSP-Tree
            *((ushort*)Buffer) = (ushort)BSPTree.Count;
            Buffer += TypeSizes.SHORT;

            foreach (RooBSPItem bspItem in BSPTree)
                bspItem.WriteTo(ref Buffer);

            // Section 2: Walls
            *((ushort*)Buffer) = (ushort)Walls.Count; 
            Buffer += TypeSizes.SHORT;

            foreach (RooWall wall in Walls)
                wall.WriteTo(ref Buffer);

            // Section 3: Unknown
            *((ushort*)Buffer) = (ushort)WallsEditor.Count; 
            Buffer += TypeSizes.SHORT;

            foreach (RooWallEditor section3Item in WallsEditor)
                section3Item.WriteTo(ref Buffer);

            // Section 4: SideDefs
            *((ushort*)Buffer) = (ushort)SideDefs.Count; 
            Buffer += TypeSizes.SHORT;

            foreach (RooSideDef sideDef in SideDefs)
                sideDef.WriteTo(ref Buffer);

            // Section 5: Sectors
            *((ushort*)Buffer) = (ushort)Sectors.Count; 
            Buffer += TypeSizes.SHORT;

            foreach (RooSector sectorDef in Sectors)
                sectorDef.WriteTo(ref Buffer);

            // Section 6: Unknown
            *((ushort*)Buffer) = (ushort)Things.Count; 
            Buffer += TypeSizes.SHORT;

            foreach (RooThing section6Item in Things)
                section6Item.WriteTo(ref Buffer);

            // RoomID
            *((int*)Buffer) = RoomID;
            Buffer += TypeSizes.INT;

            // Do the encryption
            if (EncryptionEnabled)
            {
#if WINCLR && X86
                ExpectedResponse = Crush32.Encrypt((byte*)encryptionStartOffset, (int)Buffer - encryptionStartOffset, Challenge, GetPassword());

                // Write the expected response ( was updated in Encrypt() )
                *((uint*)(encryptionStartOffset - TypeSizes.INT)) = ExpectedResponse;
#else
                throw new Exception(ERRORCRUSHPLATFORM);
#endif
            }

            //
            // server parts
            //

            Grids.WriteTo(ref Buffer);
        }

        public int ReadFrom(byte[] Buffer, int StartIndex = 0)
        {
            int cursor = StartIndex;

            uint signature = BitConverter.ToUInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            if (signature == SIGNATURE)
            {
                RooVersion = BitConverter.ToUInt32(Buffer, cursor);
                cursor += TypeSizes.INT;

                // supported version ?
                if (RooVersion >= RooFile.MINVERSION)
                {
                    Challenge = BitConverter.ToUInt32(Buffer, cursor);
                    cursor += TypeSizes.INT;

                    OffsetClient = BitConverter.ToUInt32(Buffer, cursor);
                    cursor += TypeSizes.INT;
                   
                    OffsetServer = BitConverter.ToInt32(Buffer, cursor);
                    cursor += TypeSizes.INT;
                }
                else
                    throw new Exception("RooVersion too old, got: " + RooVersion + " Expected greater " + RooFile.MINVERSION);
            }
            else
                throw new Exception("Wrong file signature: " + signature + " (expected " + SIGNATURE + ").");

            //
            // Client part (was encrypted once)
            // 

            // Check if file is encrypted
            if (BitConverter.ToUInt32(Buffer, cursor) == ENCRYPTIONFLAG)
            {
#if WINCLR && X86
                EncryptionEnabled = true;
                cursor += TypeSizes.INT;

                // get additional values for decryption
                EncryptedStreamLength = BitConverter.ToInt32(Buffer, cursor);
                cursor += TypeSizes.INT;

                ExpectedResponse = BitConverter.ToUInt32(Buffer, cursor);
                cursor += TypeSizes.INT;

                Crush32.Decrypt(Buffer, cursor, EncryptedStreamLength, Challenge, ExpectedResponse, GetPassword());
#else
                throw new Exception(ERRORCRUSHPLATFORM);
#endif
            }
            else
                EncryptionEnabled = false;

            // Get the basic infos, like room dimension and section offsets
            RoomSizeX = BitConverter.ToUInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            RoomSizeY = BitConverter.ToUInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            OffsetBSPTree = BitConverter.ToInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            OffsetWalls = BitConverter.ToInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            OffsetWallsEditor = BitConverter.ToInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            OffsetSideDefs = BitConverter.ToInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            OffsetSectors = BitConverter.ToInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            OffsetThings = BitConverter.ToInt32(Buffer, cursor);
            cursor += TypeSizes.INT;

            // used to save section item counts
            ushort len;

            // Section 1: BSP-Tree           
            cursor = OffsetBSPTree + (Convert.ToByte(EncryptionEnabled) * 12);
            len = BitConverter.ToUInt16(Buffer, cursor);
            cursor += TypeSizes.SHORT;

            BSPTree = new List<RooBSPItem>(len);
            for (int i = 0; i < len; i++)
            {
                RooBSPItem bspItem = RooBSPItem.ExtractBSPItem(Buffer, cursor);
                cursor += bspItem.ByteLength;

                BSPTree.Add(bspItem);     
            }

            // Section 2: Walls
            cursor = OffsetWalls + (Convert.ToByte(EncryptionEnabled) * 12);         
            len = BitConverter.ToUInt16(Buffer, cursor);
            cursor += TypeSizes.SHORT;

            Walls = new List<RooWall>(len);           
            for (int i = 0; i < len; i++)
            {
                RooWall lineDef = new RooWall(Buffer, cursor);               
                cursor += lineDef.ByteLength;

                lineDef.Num = i + 1;
                Walls.Add(lineDef);               
            }

            // Section 3: Unknown
            cursor = OffsetWallsEditor + (Convert.ToByte(EncryptionEnabled) * 12);
            len = BitConverter.ToUInt16(Buffer, cursor);
            cursor += TypeSizes.SHORT;

            WallsEditor = new List<RooWallEditor>(len);
            for (int i = 0; i < len; i++)
            {
                RooWallEditor section3Item = new RooWallEditor(Buffer, cursor);
                cursor += section3Item.ByteLength;

                WallsEditor.Add(section3Item);
            }

            // Section 4: SideDefs
            cursor = OffsetSideDefs + (Convert.ToByte(EncryptionEnabled) * 12);
            len = BitConverter.ToUInt16(Buffer, cursor);
            cursor += TypeSizes.SHORT;

            SideDefs = new List<RooSideDef>(len);
            for (int i = 0; i < len; i++)
            {
                RooSideDef sideDef = new RooSideDef(Buffer, cursor);
                cursor += sideDef.ByteLength;
                
                sideDef.Num = i + 1;
                sideDef.TextureChanged += OnSideTextureChanged;
                SideDefs.Add(sideDef);
            }

            // Section 5: Sectors
            cursor = OffsetSectors + (Convert.ToByte(EncryptionEnabled) * 12);
            len = BitConverter.ToUInt16(Buffer, cursor);
            cursor += TypeSizes.SHORT;

            bool hasSpeed = (RooVersion >= VERSIONSPEED);
            Sectors = new List<RooSector>(len);
            for (int i = 0; i < len; i++)
            {
                RooSector sectorDef = new RooSector(Buffer, cursor, hasSpeed);
                cursor += sectorDef.ByteLength;

                sectorDef.Num = i + 1;
                sectorDef.TextureChanged += OnSectorTextureChanged;
                sectorDef.Moved += OnSectorMoved;
                Sectors.Add(sectorDef);
            }

            // Section 6: Things
            cursor = OffsetThings + (Convert.ToByte(EncryptionEnabled) * 12);
            len = BitConverter.ToUInt16(Buffer, cursor);
            cursor += TypeSizes.SHORT;

            Things = new List<RooThing>(len);
            if (len > 2)
            {
                for (int i = 0; i < len; i++)
                {
                    RooThingExtended rooThing = new RooThingExtended(Buffer, cursor);
                    cursor += rooThing.ByteLength;

                    Things.Add(rooThing);
                }
            }
            else
            {
                for (int i = 0; i < len; i++)
                {
                    RooThing rooThing = new RooThing(Buffer, cursor);
                    cursor += rooThing.ByteLength;

                    Things.Add(rooThing);
                }
            }

            // some older maps don't have the roomid
            // so if we've already reached the serveroffset, set it to 0
            if (cursor == OffsetServer)
            {
                RoomID = 0;
            }
            else
            {
                // get roomid
                RoomID = BitConverter.ToInt32(Buffer, cursor);
                cursor += TypeSizes.INT;
            }

            //
            // Server part
            // 

            // load grids
            Grids = new RooGrids(RooVersion, Buffer, cursor);
            cursor += Grids.ByteLength;
            
            return cursor - StartIndex;
        }

        public unsafe void ReadFrom(ref byte* Buffer)
        {
            // save the startptr, we need it later
            byte* readStartPtr = Buffer;

            uint signature = *((uint*)Buffer);
            Buffer += TypeSizes.INT;

            if (signature == SIGNATURE)
            {
                RooVersion = *((uint*)Buffer);
                Buffer += TypeSizes.INT;

                // supported version ?
                if (RooVersion >= RooFile.MINVERSION)
                {
                    Challenge = *((uint*)Buffer);
                    Buffer += TypeSizes.INT;

                    OffsetClient = *((uint*)Buffer);
                    Buffer += TypeSizes.INT;
                  
                    OffsetServer = *((int*)Buffer);
                    Buffer += TypeSizes.INT;                 
                }
                else
                    throw new Exception("RooVersion too old, got: " + RooVersion + " Expected greater " + RooFile.MINVERSION);
            }
            else
                throw new Exception("Wrong file signature: " + signature + " (expected " + SIGNATURE + ").");

            //
            // Client part (was encrypted once)
            // 

            // Check if file is encrypted
            if (*((uint*)Buffer) == ENCRYPTIONFLAG)
            {
#if WINCLR && X86
                EncryptionEnabled = true;
                Buffer += TypeSizes.INT;

                // get additional values for decryption
                EncryptedStreamLength = *((int*)Buffer);
                Buffer += TypeSizes.INT;

                ExpectedResponse = *((uint*)Buffer);
                Buffer += TypeSizes.INT;

                Crush32.Decrypt(Buffer, EncryptedStreamLength, Challenge, ExpectedResponse, GetPassword());
#else
                throw new Exception(ERRORCRUSHPLATFORM); 
#endif               
            }
            else
                EncryptionEnabled = false;

            // Get the basic infos, like room dimension and section offsets
            RoomSizeX = *((uint*)Buffer);
            Buffer += TypeSizes.INT;

            RoomSizeY = *((uint*)Buffer);
            Buffer += TypeSizes.INT;

            OffsetBSPTree = *((int*)Buffer);
            Buffer += TypeSizes.INT;

            OffsetWalls = *((int*)Buffer);
            Buffer += TypeSizes.INT;

            OffsetWallsEditor = *((int*)Buffer);
            Buffer += TypeSizes.INT;

            OffsetSideDefs = *((int*)Buffer);
            Buffer += TypeSizes.INT;

            OffsetSectors = *((int*)Buffer);
            Buffer += TypeSizes.INT;

            OffsetThings = *((int*)Buffer);
            Buffer += TypeSizes.INT;

            // used to save section item counts
            ushort len;

            // Section 1: BSP-Tree
            len = *((ushort*)Buffer);
            Buffer += TypeSizes.SHORT;

            BSPTree = new List<RooBSPItem>(len);
            for (int i = 0; i < len; i++)
            {
                RooBSPItem bspItem = RooBSPItem.ExtractBSPItem(ref Buffer);
                
                BSPTree.Add(bspItem);
            }

            // Section 2: Walls
            len = *((ushort*)Buffer);
            Buffer += TypeSizes.SHORT;

            Walls = new List<RooWall>(len);
            for (int i = 0; i < len; i++)
            {
                RooWall lineDef = new RooWall(ref Buffer);
                
                lineDef.Num = i + 1;
                Walls.Add(lineDef);
            }

            // Section 3: Unknown
            len = *((ushort*)Buffer);
            Buffer += TypeSizes.SHORT;

            WallsEditor = new List<RooWallEditor>(len);
            for (int i = 0; i < len; i++)
            {
                RooWallEditor section3Item = new RooWallEditor(ref Buffer);
                
                WallsEditor.Add(section3Item);
            }

            // Section 4: SideDefs
            len = *((ushort*)Buffer);
            Buffer += TypeSizes.SHORT;

            SideDefs = new List<RooSideDef>(len);
            for (int i = 0; i < len; i++)
            {
                RooSideDef sideDef = new RooSideDef(ref Buffer);
                
                sideDef.Num = i + 1;
                sideDef.TextureChanged += OnSideTextureChanged;
                SideDefs.Add(sideDef);
            }

            // Section 5: Sectors
            len = *((ushort*)Buffer);
            Buffer += TypeSizes.SHORT;

            bool hasSpeed = (RooVersion >= VERSIONSPEED);
            Sectors = new List<RooSector>(len);
            for (int i = 0; i < len; i++)
            {
                RooSector sectorDef = new RooSector(ref Buffer, hasSpeed);
                sectorDef.TextureChanged += OnSectorTextureChanged;
                sectorDef.Moved += OnSectorMoved;
                sectorDef.Num = i + 1;
                Sectors.Add(sectorDef);
            }

            // Section 6: Things
            // There is old and new variant of "thing"
            len = *((ushort*)Buffer);
            Buffer += TypeSizes.SHORT;

            Things = new List<RooThing>(len);
            if (len > 2)
            {
                for (int i = 0; i < len; i++)
                    Things.Add(new RooThingExtended(ref Buffer));
            }
            else
            {
                for (int i = 0; i < len; i++)
                    Things.Add(new RooThing(ref Buffer));
            }

            // some older maps don't have the roomid
            // so if we've already reached the serveroffset, set it to 0
            if (Buffer - readStartPtr == OffsetServer)
            {
                RoomID = 0;
            }
            else
            {
                // roomid
                RoomID = *((int*)Buffer);
                Buffer += TypeSizes.INT;
            }

            //
            // Server part
            // 

            Grids = new RooGrids(RooVersion, ref Buffer);
        }

        public byte[] Bytes
        {
            get
            {
                byte[] returnValue = new byte[ByteLength];
                WriteTo(returnValue);
                return returnValue;
            }

            set
            {
                ReadFrom(value);
            }
        }
        #endregion

        #region IGameFile
        public unsafe void Load(string FilePath)
        {
            Filename = Path.GetFileNameWithoutExtension(FilePath);
            byte[] FileBytes = File.ReadAllBytes(FilePath);
            
            //ReadFrom(FileBytes, 0);
            fixed (byte* ptrBytes = FileBytes)
            {
                byte* ptr = ptrBytes;
                ReadFrom(ref ptr);
            }

            // resolve links (creates object references in models out of loaded indices)
            ResolveIndices();

            // more after load code
            DoCalculations();
        }

        public void Save(string FilePath)
        {
            File.WriteAllBytes(FilePath, Bytes);
        }
        
        public string Filename { get; set; }        
        #endregion

        #region Properties
        /// <summary>
        /// Version of this file/instance.
        /// </summary>
        public uint RooVersion { get; set; }

        /// <summary>
        /// Checksum against room manipulation.
        /// </summary>
        public uint Challenge { get; set; }

        /// <summary>
        /// The offset of the client data sections start
        /// </summary>
        public uint OffsetClient { get; private set; }
        
        /// <summary>
        /// The offset of the server data section to start
        /// </summary>
        public int OffsetServer { get; set; }

        /// <summary>
        /// Whether or not encryption is enabled.
        /// </summary>
        public bool EncryptionEnabled { get; set; }

        /// <summary>
        /// Length of encrypted stream.
        /// </summary>
        public int EncryptedStreamLength { get; protected set; }

        /// <summary>
        /// Used to verify decryption password.
        /// </summary>
        public uint ExpectedResponse { get; protected set; }
        
        /// <summary>
        /// Length of the room.
        /// </summary>
        public uint RoomSizeX { get; set; }
        
        /// <summary>
        /// Width of the room.
        /// </summary>
        public uint RoomSizeY { get; set; }

        /// <summary>
        /// Offset of BSPTree section in bytes.
        /// </summary>
        public int OffsetBSPTree { get; protected set; }

        /// <summary>
        /// Offset of walls section in bytes.
        /// </summary>
        public int OffsetWalls { get; protected set; }

        /// <summary>
        /// Offset of walls (used in windeu) section in bytes.
        /// </summary>
        public int OffsetWallsEditor { get; protected set; }

        /// <summary>
        /// Offset of SideDefs section in bytes.
        /// </summary>
        public int OffsetSideDefs { get; protected set; }

        /// <summary>
        /// Offset of sectors section in bytes.
        /// </summary>
        public int OffsetSectors { get; protected set; }

        /// <summary>
        /// Offset of Things section in bytes.
        /// </summary>
        public int OffsetThings { get; protected set; }
        
        /// <summary>
        /// The populated BSP tree for this room.
        /// </summary>
        public List<RooBSPItem> BSPTree { get; set; }
       
        /// <summary>
        /// Existing walls/linedefs in this room, as used by the client.
        /// </summary>
        public List<RooWall> Walls { get; set; }

        /// <summary>
        /// Existing walls/linedefs in this room, as used by WINDEU.
        /// </summary>
        public List<RooWallEditor> WallsEditor { get; set; }
        
        /// <summary>
        /// Wall-sides in the room.
        /// Each Wall has 2 sides (with up to 3 parts).
        /// </summary>
        public List<RooSideDef> SideDefs { get; set; }
        
        /// <summary>
        /// Sectors in a room are composed by referenced SubSectors (convex-polygons).
        /// SubSectors are part of the BSP-tree.
        /// </summary>
        public List<RooSector> Sectors { get; set; }

        /// <summary>
        /// Things data.
        /// </summary>
        public List<RooThing> Things { get; set; }
        
        /// <summary>
        /// RoomID (Note: This is mostly unset or uncorrect).
        /// WinDEU always saves it (save.cpp), but only loads it for NumThings > 2 (load.cpp)
        /// </summary>
        public int RoomID { get; set; }

        /// <summary>
        /// 2D server grids for the room
        /// </summary>
        public RooGrids Grids { get; set; }

        /// <summary>
        /// Returns all BSP tree-nodes of type RooPartitionLine
        /// from BSPTree property.
        /// </summary>
        public List<RooPartitionLine> BSPTreeNodes
        {
            get
            {
                List<RooPartitionLine> list = new List<RooPartitionLine>();

                foreach (RooBSPItem item in BSPTree)
                {
                    if (item.Type == RooBSPItem.PartitionLineType)
                        list.Add((RooPartitionLine)item);
                }

                return list;
            }
        }

        /// <summary>
        /// Returns all BSP tree-nodes of type RooSubSector
        /// from BSPTree property.
        /// </summary>
        public List<RooSubSector> BSPTreeLeaves
        {
            get
            {
                List<RooSubSector> list = new List<RooSubSector>();

                foreach (RooBSPItem item in BSPTree)
                {
                    if (item.Type == RooBSPItem.SubSectorType)
                        list.Add((RooSubSector)item);
                }

                return list;
            }
        }

        /// <summary>
        /// True if the indices read from file have been resolved to object references.
        /// See ResolveIndices().
        /// </summary>
        public bool IsIndicesResolved { get; protected set; }
        
        /// <summary>
        /// True if the required texture resources have been resolved to references.
        /// See ResolveResources().
        /// </summary>
        public bool IsResourcesResolved { get; protected set; }

        #endregion

        #region Constructors
        /// <summary>
        /// Empty constructor
        /// </summary>
        public RooFile()
        {
            BSPTree = new List<RooBSPItem>();
            Walls = new List<RooWall>();
            WallsEditor = new List<RooWallEditor>();
            SideDefs = new List<RooSideDef>();
            Sectors = new List<RooSector>();
            Things = new List<RooThing>();
        }

        /// <summary>
        /// Constructor by file
        /// </summary>
        /// <param name="FilePath"></param>
        public RooFile(string FilePath)
        {
            Load(FilePath);
        }
        #endregion
        
        #region Methods
        /// <summary>
        /// Gets password based on Version
        /// </summary>
        /// <returns></returns>
        public byte[] GetPassword()
        {
            // ROO versions 9/10/11
            if (RooVersion <= 11)
                return PASSWORDV10;

            // all others
            else return PASSWORDV12;
        }

        /// <summary>
        /// Resolves all index references to real object references
        /// </summary>
        public virtual void ResolveIndices()
        {
            foreach (RooWall wall in Walls)
                wall.ResolveIndices(this);

            foreach (RooBSPItem item in BSPTree)
                item.ResolveIndices(this);

            IsIndicesResolved = true;
        }

        /// <summary>
        /// Uses a ResourceManager to resolve/load all resources (textures) references from
        /// </summary>
        /// <param name="M59ResourceManager"></param>
        public virtual void ResolveResources(ResourceManager M59ResourceManager)
        {
            foreach (RooSector sector in Sectors)
                sector.ResolveResources(M59ResourceManager, false);

            foreach (RooSideDef wallSide in SideDefs)
                wallSide.ResolveResources(M59ResourceManager, false);

            IsResourcesResolved = true;
        }

        /// <summary>
        /// Uncompresses all attached resources
        /// </summary>
        public void UncompressAll()
        {
            foreach (RooSector sector in Sectors)
            {
                if (sector.ResourceCeiling != null)
                    foreach (BgfBitmap bgf in sector.ResourceCeiling.Frames)
                        if (bgf.IsCompressed)
                            bgf.IsCompressed = false;

                if (sector.ResourceFloor != null)
                    foreach (BgfBitmap bgf in sector.ResourceFloor.Frames)
                        if (bgf.IsCompressed)
                            bgf.IsCompressed = false;
            }

            foreach (RooSideDef side in SideDefs)
            {
                if (side.ResourceUpper!= null)
                    foreach (BgfBitmap bgf in side.ResourceUpper.Frames)
                        if (bgf.IsCompressed)
                            bgf.IsCompressed = false;

                if (side.ResourceMiddle != null)
                    foreach (BgfBitmap bgf in side.ResourceMiddle.Frames)
                        if (bgf.IsCompressed)
                            bgf.IsCompressed = false;

                if (side.ResourceLower != null)
                    foreach (BgfBitmap bgf in side.ResourceLower.Frames)
                        if (bgf.IsCompressed)
                            bgf.IsCompressed = false;
            }          
        }

        /// <summary>
        /// Runs all additional computations (i.e. wall heights)
        /// </summary>
        public virtual void DoCalculations()
        {
            foreach (RooWall wall in Walls)
                wall.CalculateWallSideHeights();
        }

        /// <summary>
        /// Looks up the height of a point from the sector it's included in.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>        
        /// <param name="SubSector">The subsector the point was found in or NULL</param>
        /// <param name="IsFloor"></param>
        /// <param name="WithSectorDepth"></param>
        /// <returns>Height of point or -1 if no sector found for point</returns>
        public Real GetHeightAt(int x, int y, out RooSubSector SubSector, bool IsFloor = true, bool WithSectorDepth = false)
        {
            SubSector = null;

            // walk BSP tree and get subsector containing point
            SubSector = GetSubSectorAt(x, y);

            if (SubSector != null && SubSector.Sector != null)
            {
                if (IsFloor)
                    return SubSector.Sector.CalculateFloorHeight(x, y, WithSectorDepth);

                else
                    return SubSector.Sector.CalculateCeilingHeight(x, y);
            }

            return -1;
        }
        
        /// <summary>
        /// Returns the subsector containing a given point in ROO coordinates.
        /// Starts searching from Root node.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns>SubSector (leaf) or null</returns>
        public RooSubSector GetSubSectorAt(int x, int y)
        {
            if (BSPTree.Count > 0)
                return (RooSubSector)GetSubSectorAt(BSPTree[0], x, y);
            
            else return null;
        }

        /// <summary>
        /// Recursively walks the BSP tree start at given node,
        /// to find the subsector (leaf) containing the point.
        /// </summary>
        /// <param name="node"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        protected RooBSPItem GetSubSectorAt(RooBSPItem node, int x, int y)
        {
            if (node == null)
                return null;

            int side;

            if (node.Type == RooBSPItem.SubSectorType)
                return node;
            else
            {
                RooPartitionLine line = (RooPartitionLine)node;
                side = line.A * x + line.B * y + line.C;

                if (side == 0)
                {
                    if (line.RightChild != null)
                    {
                        return GetSubSectorAt(line.RightChild, x, y);
                    }
                    else
                    {
                        return GetSubSectorAt(line.LeftChild, x, y);
                    }                   
                }
                else if (side > 0)
                {
                    return GetSubSectorAt(line.RightChild, x, y);
                }
                else
                {
                    return GetSubSectorAt(line.LeftChild, x, y);
                }
            }
        }
      
        /// <summary>
        /// Returns the sidedef matching the given server id
        /// </summary>
        /// <param name="ServerID"></param>
        /// <returns></returns>
        public RooSideDef GetSideByServerID(int ServerID)
        {
            foreach (RooSideDef sideDef in SideDefs)
                if (sideDef.ServerID == ServerID)
                    return sideDef;
            
            return null;
        }
        
        /// <summary>
        /// Returns the boundingbox of this room.
        /// Z is height. Scale is ROO.
        /// </summary>
        /// <returns></returns>
        public Tuple<V3, V3> GetBoundingBox()
        {
            Real minheight = 0.0f;
            Real maxheight = 1.0f;

            if (Sectors.Count > 0)
            {
                minheight = Sectors[0].FloorHeight;
                maxheight = Sectors[0].CeilingHeight;

                foreach (RooSector sector in Sectors)
                {
                    if (sector.FloorHeight < minheight)
                        minheight = sector.FloorHeight;

                    if (sector.CeilingHeight > maxheight)
                        maxheight = sector.CeilingHeight;
                }
            }

            // convert height to scale of width/length
            minheight = minheight * 16.0f;
            maxheight = maxheight * 16.0f;

            V3 min = new V3(0.0f, 1.0f, minheight);
            V3 max = new V3(0.0f, 1.0f, maxheight);

            if (BSPTree.Count > 0)
            {
                min.X = BSPTree[0].X1;
                min.Y = BSPTree[0].Y1;

                max.X = BSPTree[0].X2;
                max.Y = BSPTree[0].Y2;
            }

            return new Tuple<V3, V3>(min, max);
        }

        /// <summary>
        /// Processes updates at runtime, like animations.
        /// Call regularly.
        /// </summary>
        /// <param name="Tick"></param>
        /// <param name="Span"></param>
        public void Tick(long Tick, long Span)
        {
            // update wall animations
            foreach (RooSideDef wallSide in SideDefs)
                if (wallSide.Animation != null)
                    wallSide.Animation.Tick(Tick, Span);

            // update sectors
            foreach (RooSector sector in Sectors)
                sector.Tick(Tick, Span);
        }

        /// <summary>
        /// Returns a list of strings representing all used material names.
        /// </summary>
        /// <returns></returns>
        public List<string> GetAllMaterialNames()
        {
            List<string> list = new List<string>();

            // add materials used on sector floors & ceilings
            foreach (RooSector obj in Sectors)
            {
                if (!list.Contains(obj.MaterialNameFloor))
                    list.Add(obj.MaterialNameFloor);

                if (!list.Contains(obj.MaterialNameCeiling))
                    list.Add(obj.MaterialNameCeiling);
            }

            // add materials used on sides
            foreach (RooSideDef obj in SideDefs)
            {
                if (!list.Contains(obj.MaterialNameLower))
                    list.Add(obj.MaterialNameLower);

                if (!list.Contains(obj.MaterialNameMiddle))
                    list.Add(obj.MaterialNameMiddle);

                if (!list.Contains(obj.MaterialNameUpper))
                    list.Add(obj.MaterialNameUpper);
            }

            return list;
        }

        /// <summary>
        /// Checks a movement vector (Start->End) for wall collisions and
        /// returns a possibly adjusted movement vector to use instead.
        /// This might be a slide along a wall or a zero-vector for a full block.
        /// </summary>
        /// <param name="Start">Startpoint of movement (in ROO coords)</param>
        /// <param name="End">Endpoint of movement (in ROO coords)</param>
        /// <param name="PlayerHeight">Height of the player for ceiling collisions</param>
        /// <returns>Same or adjusted delta vector between Start and end (in ROO coords)</returns>
        public V2 VerifyMove(V3 Start, V2 End, Real PlayerHeight)
        {                       
            V2 Start2D = new V2(Start.X, Start.Z);

            // the move, if everything OK with this move
            // this is the untouched return
            V2 diff = End - Start2D;

            // an extension to the delta for min. wall distance
            V2 ext = diff.Clone();
            ext.ScaleToLength(GeometryConstants.WALLMINDISTANCE);
            
            // this holds a possible collision wall
            RooWall intersectWall = null;

            // look for blocking wall
            foreach (RooWall wall in Walls)
            {
                // check if the wall blocks the move including wallmin extension
                if (wall.IsBlocking(Start, End + ext, PlayerHeight))
                {
                    intersectWall = wall;
                    break;
                }
            }

            // if there was an intersection, try to slide along wall
            if (intersectWall != null)
            {
                V2 newend = intersectWall.SlideAlong(Start2D, End);
                diff = newend - Start2D;

                // an extension to the delta for min. wall distance
                ext = diff.Clone();
                ext.ScaleToLength(GeometryConstants.WALLMINDISTANCE);

                // recheck slide for intersect with other walls
                // this is necessary in case you get into a corner
                // to not slide through other wall there
                foreach (RooWall wall in Walls)
                {
                    // check if the wall blocks the move including wallmin extension
                    if (wall.IsBlocking(Start, newend + ext, PlayerHeight))
                    {
                        diff.X = 0;
                        diff.Y = 0;
                        break;
                    }
                }
            }

            return diff;
        }

        /// <summary>
        /// Verifies if any wall blocks a ray from Start to End
        /// </summary>
        /// <param name="Start"></param>
        /// <param name="End"></param>
        /// <returns>True if OK, false if collision.</returns>
        public bool VerifySight(V3 Start, V3 End)
        {
            // look for blocking wall
            foreach (RooWall wall in Walls)            
                if (wall.IsBlockingSight(Start, End))                
                    return false;
                
            return true;
        }

        /// <summary>
        /// Small helper function which tries to create a heightmap from roomdata.
        /// Not specifically used right now.
        /// </summary>
        /// <param name="Size"></param>
        /// <param name="IsFloor"></param>
        /// <param name="WithSectorDepths"></param>
        /// <returns></returns>
        public Real[] GenerateHeightMap(uint Size, bool IsFloor, bool WithSectorDepths)
	    {		
		    // allocate array to store heights
            Real[] heights = new Real[Size * Size];

            // get the scale factor between longer side and size
            Real scale;
            if (RoomSizeX > RoomSizeY)
            {
                scale = (Real)RoomSizeX / (Real)Size;
            }
            else
            {
                scale = (Real)RoomSizeY / (Real)Size;
            }

		    RooSubSector subsector;

            // iterate rows of heightmap
            for (uint i = 0; i < Size; i++)
            {
                // iterate columns of heightmap
                for (uint j = 0; j < Size; j++)
                {
                    // get mapcoordinates for this pixel
                    Real x = i * scale;
                    Real y = j * scale;

                    // lookup the height from roomdata
                    Real height = GetHeightAt(Convert.ToInt32(x), Convert.ToInt32(y), out subsector, IsFloor, WithSectorDepths);
                    
                    // process it
                    if (height == -1.0f && (i > 0.0f || j > 0.0f))
                    {
                        // use prece
                        heights[(i * Size) + j] = heights[(i * Size) + j - 1];
                    }
                    else
                    {
                        heights[(i * Size) + j] = height / scale;
                    }
                }
            }

            return heights;
	    }

        /// <summary>
        /// Executed when sector moved.
        /// Raised SectorMoved event.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected void OnSectorMoved(object sender, EventArgs e)
        {
            RooSector sector = (RooSector)sender;

            if (SectorMoved != null)
                SectorMoved(this, new SectorMovedEventArgs(sector));
        }

        /// <summary>
        /// Executed when walltexture changed, triggers event
        /// </summary>
        protected void OnSideTextureChanged(object sender, WallTextureChangedEventArgs e)
        {
            if (WallTextureChanged != null)
                WallTextureChanged(this, e);
        }

        /// <summary>
        /// Executed when sectortexture changed, triggers event
        /// </summary>
        protected void OnSectorTextureChanged(object sender, SectorTextureChangedEventArgs e)
        {
            if (SectorTextureChanged != null)
                SectorTextureChanged(this, e);
        }
        #endregion

        #region Message handlers
        /// <summary>
        /// Handles some GameMode messages
        /// </summary>
        /// <param name="Message"></param>
        public void HandleGameModeMessage(GameModeMessage Message)
        {
            switch ((MessageTypeGameMode)Message.PI)
            {
                case MessageTypeGameMode.ChangeTexture:
                    HandleChangeTexture((ChangeTextureMessage)Message);
                    break;

                case MessageTypeGameMode.SectorMove:
                    HandleSectorMove((SectorMoveMessage)Message);
                    break;
            }
        }

        /// <summary>
        /// Handle ChangeTextureMessage
        /// </summary>
        /// <param name="Message"></param>
        protected void HandleChangeTexture(ChangeTextureMessage Message)
        {
            TextureChangeInfo info = Message.TextureChangeInfo;

            // TEXTURE CHANGE ON SIDE
            if (info.Flags.IsAboveWall ||
                info.Flags.IsNormalWall ||
                info.Flags.IsBelowWall)
            {
                foreach (RooSideDef side in SideDefs)
                {
                    if (side.ServerID == info.ServerID)
                    {
                        if (info.Flags.IsAboveWall)
                            side.SetUpperTexture(info.TextureNum, info.Resource);

                        else if (info.Flags.IsNormalWall)
                            side.SetMiddleTexture(info.TextureNum, info.Resource);

                        else if (info.Flags.IsBelowWall)
                            side.SetLowerTexture(info.TextureNum, info.Resource);
                    }
                }
            }

            // TEXTURE CHANGE ON SECTOR
            else if (info.Flags.IsCeiling || info.Flags.IsFloor)
            {
                foreach (RooSector sector in Sectors)
                {
                    if (sector.ServerID == info.ServerID)
                    {
                        if (info.Flags.IsFloor)
                            sector.SetFloorTexture(info.TextureNum, info.Resource);

                        else if (info.Flags.IsCeiling)
                            sector.SetCeilingTexture(info.TextureNum, info.Resource);
                    }
                }
            }
        }

        /// <summary>
        /// Handle SectorMoveMessage
        /// </summary>
        /// <param name="Message"></param>
        protected void HandleSectorMove(SectorMoveMessage Message)
        {
            SectorMove info = Message.SectorMove;

            foreach (RooSector sector in Sectors)
            {
                // start / adjust movement on matching sectors
                if (sector.ServerID == info.SectorNr)
                    sector.StartMove(info);
            }
        }
        #endregion

        /// <summary>
        /// Returns a name based on a name convention for textures.
        /// Basically the bgf filename, the frameindex added,
        /// and a dummy extension to potentially load it from a file
        /// Example: "grd00024-0.png"
        /// </summary>
        /// <param name="TextureFile"></param>
        /// <param name="FrameIndex"></param>
        /// <returns></returns>
        public static string GetNameForTexture(BgfFile TextureFile, int FrameIndex)
        {
            return TextureFile.Filename + '-' + FrameIndex + FileExtensions.PNG;
        }

        /// <summary>
        /// Returns a name based on a name convention for Mogre from Bgf.
        /// </summary>
        /// <param name="TextureFile"></param>
        /// <param name="FrameIndex"></param>
        /// <param name="ScrollSpeed"></param>
        /// <returns></returns>
        public static string GetNameForMaterial(BgfFile TextureFile, int FrameIndex, V2 ScrollSpeed)
        {
            // examplereturn (0 is index in bgf)
            // Room/grd00352-0
            string matName = "Room/" + TextureFile.Filename + "-" + FrameIndex;

            // add appendix for scrollspeed
            // Room/grd00352-0-SCROLL-1.0-0.0
            matName += "-SCROLL-" + ScrollSpeed.X.ToString() + "-" + ScrollSpeed.Y.ToString();

            return matName;
        }    
    }
}
